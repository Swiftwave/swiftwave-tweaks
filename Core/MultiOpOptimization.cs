using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>Primitive operation kinds executed by <see cref="MultiOpOptimization"/>.</summary>
public enum OpKind
{
    RegDword,
    RegString,
    RegBinary,
    RegDelete,
    ServiceStart,
    TaskEnable,
    NetshCommand,
    BcdEdit,
    PowerScheme,
    EtwChannel,
}

/// <summary>One ordered operation. Registry/service/Etw share Path+Name; tasks use TaskPath.</summary>
public sealed record Operation(
    OpKind Kind,
    RegistryHive Hive = RegistryHive.LocalMachine,
    RegistryView View = RegistryView.Registry64,
    string Path = "",
    string Name = "",
    int IntValue = 0,
    string StringValue = "",
    byte[]? BinaryValue = null,
    string ServiceName = "",
    string TaskPath = "",
    string Command = "",
    bool BoolValue = false
);

/// <summary>
/// Typed prior state for one operation. <see cref="PriorData"/> is always a string so it
/// round-trips through System.Text.Json without becoming a <c>JsonElement</c> (the prior
/// implementation stored <c>object</c> and silently lost its type on deserialize — a D-2 bug).
/// Encoding: DWord → decimal, String/ExpandString → raw, Binary → base64, MultiString → JSON array.
/// </summary>
public sealed record OperationPriorState(
    OpKind Kind,
    bool Existed,
    string? PriorKind,
    string? PriorData,
    int PriorServiceStart,
    bool PriorTaskEnabled,
    bool PriorTaskExists,
    string CommandOutput
);

/// <summary>
/// Base class for multi-operation optimizations with exact per-operation capture and
/// reverse-order rollback (Hone defects D-2/D-4), plus journal-only state (C3/C4).
/// </summary>
public abstract class MultiOpOptimization : Optimization
{
    protected abstract IReadOnlyList<Operation> Operations { get; }

    /// <summary>Optional override to present a different prior for an operation (shared ownership).</summary>
    protected virtual OperationPriorState? AdoptPrior(Operation op) => null;

    /// <summary>Called after a successful apply — used to transfer ownership from legacy features.</summary>
    protected virtual void OnApplied() { }

    /// <summary>Called after revert completes.</summary>
    protected virtual void OnRolledBack(bool failed) { }

    /// <summary>Inverse of a command operation (netsh/bcdedit). Return null on success.</summary>
    protected virtual string? RestoreCommand(Operation op) => null;

    protected virtual (int ExitCode, string Output) RunCommand(string fileName, string arguments)
    {
        var r = ProcessRunner.RunCapture(fileName, arguments);
        return r is null ? (-1, "") : r.Value;
    }

    private List<OperationPriorState> _priorStates = new();
    private readonly HashSet<int> _skipped = new();

    /// <summary>Independent re-read using the feature's own Detect implementation.</summary>
    public override Detection DetectAfterApply() => Detect();

    // ------------------------------------------------------------------ capture

    protected sealed override string CapturePrevious()
    {
        var prior = new List<OperationPriorState>(Operations.Count);
        foreach (var op in Operations)
            prior.Add(AdoptPrior(op) ?? CaptureOne(op));
        _priorStates = prior;
        return JsonSerializer.Serialize(prior);
    }

    private OperationPriorState CaptureOne(Operation op) => op.Kind switch
    {
        OpKind.RegDword or OpKind.RegString or OpKind.RegBinary or OpKind.RegDelete or OpKind.EtwChannel => CaptureReg(op),
        OpKind.ServiceStart => CaptureService(op),
        OpKind.TaskEnable => CaptureTask(op),
        OpKind.PowerScheme => new OperationPriorState(op.Kind, true, "String",
            PowerSchemeService.ReadActiveScheme(), 0, false, false, ""),
        _ => new OperationPriorState(op.Kind, false, null, null, 0, false, false, "")
    };

    private static OperationPriorState CaptureReg(Operation op)
    {
        if (!RegHelper.ValueExists(op.Hive, op.View, op.Path, op.Name))
            return new OperationPriorState(op.Kind, false, null, null, 0, false, false, "");

        var kind = RegHelper.ReadKind(op.Hive, op.View, op.Path, op.Name);
        var value = RegHelper.ReadValue(op.Hive, op.View, op.Path, op.Name);
        string? encoded = kind switch
        {
            RegistryValueKind.DWord => value is int i ? i.ToString() : null,
            RegistryValueKind.QWord => value is long l ? l.ToString() : null,
            RegistryValueKind.Binary => value is byte[] b ? Convert.ToBase64String(b) : null,
            RegistryValueKind.MultiString => value is string[] a ? JsonSerializer.Serialize(a) : null,
            _ => value?.ToString()
        };
        return new OperationPriorState(op.Kind, true, kind?.ToString(), encoded, 0, false, false, "");
    }

    private static OperationPriorState CaptureService(Operation op)
    {
        int? start = RegHelper.ReadDword(op.Hive, op.View, op.Path, op.Name);
        return new OperationPriorState(op.Kind, start is not null, "DWord",
            start?.ToString(), start ?? 0, false, false, "");
    }

    private static OperationPriorState CaptureTask(Operation op)
    {
        bool? enabled = ScheduledTaskService.IsEnabled(op.TaskPath);
        return new OperationPriorState(op.Kind, enabled is not null, null, null, 0,
            enabled ?? false, enabled is not null, "");
    }

    // ------------------------------------------------------------------ apply

    protected sealed override bool ApplyChange(out string detail)
    {
        _skipped.Clear();
        var applied = new List<int>();
        Dictionary<int, (string? Error, bool Skipped)>? taskResults = null;
        for (int i = 0; i < Operations.Count; i++)
        {
            if (Operations[i].Kind == OpKind.TaskEnable && taskResults is null)
            {
                int count = 1;
                while (i + count < Operations.Count && Operations[i + count].Kind == OpKind.TaskEnable)
                    count++;
                var batch = Operations.Skip(i).Take(count)
                    .Select(op => (op.TaskPath, op.BoolValue)).ToArray();
                var results = ScheduledTaskService.SetEnabled(batch);
                taskResults = new Dictionary<int, (string? Error, bool Skipped)>();
                for (int j = 0; j < results.Count; j++)
                {
                    var ok = results[j];
                    taskResults[i + j] = ok is null
                        ? (null, true)
                        : (ok.Value ? null : "task operation rejected", false);
                }
            }
            else if (Operations[i].Kind != OpKind.TaskEnable)
            {
                taskResults = null;
            }
            var (err, skipped) = Operations[i].Kind == OpKind.TaskEnable
                ? taskResults![i]
                : ApplyOne(Operations[i]);
            if (err is not null)
            {
                // Partial-application failure: undo everything already written, in reverse order,
                // so a mid-sequence failure never leaves half a feature applied (E-PARTIAL-FAILURE).
                var undoFailures = new List<string>();
                for (int j = applied.Count - 1; j >= 0; j--)
                {
                    int idx = applied[j];
                    var undo = RevertOne(Operations[idx], _priorStates.ElementAtOrDefault(idx)
                        ?? new OperationPriorState(Operations[idx].Kind, false, null, null, 0, false, false, ""));
                    if (undo is not null) undoFailures.Add($"#{idx}: {undo}");
                }
                detail = $"E-PARTIAL-FAILURE — operation #{i} ({Operations[i].Kind} " +
                         $"{Operations[i].Path}\\{Operations[i].Name} {Operations[i].Command}) failed: {err}" +
                         (undoFailures.Count == 0
                             ? "; earlier operations were rolled back."
                             : "; rollback of earlier operations also failed: " + string.Join("; ", undoFailures));
                return false;
            }
            if (skipped) _skipped.Add(i);
            else applied.Add(i);
        }

        var targets = new List<JournalTarget>();
        for (int i = 0; i < Operations.Count; i++)
        {
            if (_skipped.Contains(i)) continue;
            var op = Operations[i];
            if (!Verifiable(op.Kind)) continue;
            targets.Add(new JournalTarget
            {
                Target = TargetKey(op),
                Kind = op.Kind.ToString(),
                Prior = PriorRaw(_priorStates.ElementAtOrDefault(i)),
                New = NewRaw(op)
            });
        }
        Journal.Commit(Id, targets);
        OnApplied();
        detail = "";
        return true;
    }

    private (string? Error, bool Skipped) ApplyOne(Operation op)
    {
        try
        {
            switch (op.Kind)
            {
                case OpKind.RegDword:
                case OpKind.EtwChannel:
                    RegHelper.WriteDword(op.Hive, op.Path, op.Name, op.IntValue);
                    return (null, false);
                case OpKind.ServiceStart:
                {
                    // Never create a Services\<name> key for a service this machine does not have —
                    // that would fabricate a phantom service entry (spec E-SVC-ABSENT).
                    if (!RegHelper.KeyExists(op.Hive, op.View, op.Path))
                        return (null, true);
                    RegHelper.WriteDword(op.Hive, op.Path, op.Name, op.IntValue);
                    return (null, false);
                }
                case OpKind.RegString:
                    RegHelper.WriteString(op.Hive, op.Path, op.Name, op.StringValue);
                    return (null, false);
                case OpKind.RegBinary:
                    RegHelper.WriteBinary(op.Hive, op.Path, op.Name, op.BinaryValue ?? Array.Empty<byte>());
                    return (null, false);
                case OpKind.RegDelete:
                    RegHelper.DeleteValue(op.Hive, op.Path, op.Name);
                    return (null, false);
                case OpKind.NetshCommand:
                case OpKind.BcdEdit:
                {
                    var (code, _) = RunCommand(
                        op.Kind == OpKind.NetshCommand ? "netsh.exe" : "bcdedit.exe", op.Command);
                    return (code == 0 ? null : $"exit code {code}", false);
                }
                case OpKind.PowerScheme:
                    return (PowerSchemeService.Activate(op.StringValue) ? null : "powercfg /setactive failed", false);
                default:
                    return ("unknown operation kind", false);
            }
        }
        catch (Exception ex) { return (ex.Message, false); }
    }

    // ------------------------------------------------------------------ verify / state

    /// <summary>Journal-only state for the features whose operations are read-back verifiable.</summary>
    protected Detection JournalDetection(string optimizedText)
    {
        var current = Operations
            .Where(op => Verifiable(op.Kind))
            .Select(op => (Op: op, Cur: CurrentRaw(op) ?? ""))
            // An absent service/task/value is not a target on this machine: exclude it so its
            // empty reading can never be mistaken for a foreign edit.
            .Where(x => !(x.Cur.Length == 0 && x.Op.Kind != OpKind.RegDelete))
            .Select(x => (Target: TargetKey(x.Op), Actual: (string?)x.Cur))
            .ToList();
        return V3State.Evaluate(Id, current, optimizedText, "Not applied (no journal transaction).");
    }

    // ------------------------------------------------------------------ revert

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        List<OperationPriorState>? priors;
        try { priors = JsonSerializer.Deserialize<List<OperationPriorState>>(previousState); }
        catch (Exception ex) { detail = "Prior state unreadable: " + ex.Message; return false; }
        if (priors is null || priors.Count != Operations.Count)
        { detail = "Prior state does not match the operation set."; return false; }

        var failures = new List<string>();
        for (int i = priors.Count - 1; i >= 0; i--)
        {
            var err = RevertOne(Operations[i], priors[i]);
            if (err is not null) failures.Add($"#{i} {Operations[i].Kind}: {err}");
        }

        Journal.MarkRolledBack(Id, failures.Count > 0);
        OnRolledBack(failures.Count > 0);
        detail = failures.Count == 0 ? "" : "E-PARTIAL-FAILURE — " + string.Join("; ", failures);
        return failures.Count == 0;
    }

    private string? RevertOne(Operation op, OperationPriorState prior)
    {
        try
        {
            switch (op.Kind)
            {
                case OpKind.RegDword:
                case OpKind.RegString:
                case OpKind.RegBinary:
                case OpKind.RegDelete:
                case OpKind.EtwChannel:
                    return RestoreReg(op, prior);
                case OpKind.ServiceStart:
                    if (prior.Existed) RegHelper.WriteDword(op.Hive, op.Path, op.Name, prior.PriorServiceStart);
                    else RegHelper.DeleteValue(op.Hive, op.Path, op.Name);
                    return null;
                case OpKind.TaskEnable:
                    if (prior.Existed && prior.PriorTaskEnabled)
                    {
                        // A task skipped during apply is still enabled; re-enabling it would fail
                        // on ACL-protected tasks and wrongly report the rollback as failed.
                        if (ScheduledTaskService.IsEnabled(op.TaskPath) == true) return null;
                        return ScheduledTaskService.ReEnable(op.TaskPath) ? null : "could not re-enable task";
                    }
                    return null;
                case OpKind.PowerScheme:
                    if (prior.Existed && prior.PriorData is { Length: 36 } guid)
                        return PowerSchemeService.Activate(guid) ? null : "could not reactivate prior scheme";
                    return null;
                case OpKind.NetshCommand:
                    return RestoreCommand(op);
                case OpKind.BcdEdit:
                    return RestoreCommand(op);
                default:
                    return null;
            }
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string? RestoreReg(Operation op, OperationPriorState prior)
    {
        if (!prior.Existed)
        {
            RegHelper.DeleteValue(op.Hive, op.Path, op.Name);
            return null;
        }

        var kind = Enum.TryParse<RegistryValueKind>(prior.PriorKind, out var k) ? k : RegistryValueKind.DWord;
        object data = kind switch
        {
            RegistryValueKind.DWord => int.Parse(prior.PriorData ?? "0"),
            RegistryValueKind.QWord => long.Parse(prior.PriorData ?? "0"),
            RegistryValueKind.Binary => Convert.FromBase64String(prior.PriorData ?? ""),
            RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(prior.PriorData ?? "[]") ?? Array.Empty<string>(),
            _ => prior.PriorData ?? ""
        };
        using var key = RegistryKey.OpenBaseKey(op.Hive, op.View).CreateSubKey(op.Path, writable: true);
        key!.SetValue(op.Name, data, kind);
        return null;
    }

    // ------------------------------------------------------------------ journal helpers

    private static bool Verifiable(OpKind kind) =>
        kind is not (OpKind.NetshCommand or OpKind.BcdEdit);

    protected static string TargetKey(Operation op) => op.Kind switch
    {
        OpKind.TaskEnable => "TASK:" + op.TaskPath,
        OpKind.PowerScheme => "POWER:ACTIVE",
        OpKind.NetshCommand => "CMD:netsh " + op.Command,
        OpKind.BcdEdit => "CMD:bcdedit " + op.Command,
        _ => $"REG:{(op.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}\\{op.Path}\\{op.Name}"
    };

    protected static string NewRaw(Operation op) => op.Kind switch
    {
        OpKind.RegDword or OpKind.ServiceStart or OpKind.EtwChannel => op.IntValue.ToString(),
        OpKind.RegString => op.StringValue,
        OpKind.RegBinary => Convert.ToBase64String(op.BinaryValue ?? Array.Empty<byte>()),
        OpKind.RegDelete => "",
        OpKind.TaskEnable => op.BoolValue ? "enabled" : "disabled",
        OpKind.PowerScheme => op.StringValue,
        _ => ""
    };

    protected static string? CurrentRaw(Operation op)
    {
        switch (op.Kind)
        {
            case OpKind.TaskEnable:
            {
                var enabled = ScheduledTaskService.IsEnabled(op.TaskPath);
                return enabled is null ? "" : enabled.Value ? "enabled" : "disabled";
            }
            case OpKind.PowerScheme:
                return PowerSchemeService.ReadActiveScheme();
            case OpKind.RegDword:
            case OpKind.ServiceStart:
            case OpKind.EtwChannel:
                return RegHelper.ReadDword(op.Hive, op.View, op.Path, op.Name)?.ToString() ?? "";
            case OpKind.RegString:
                return RegHelper.ReadValue(op.Hive, op.View, op.Path, op.Name)?.ToString() ?? "";
            case OpKind.RegBinary:
                return RegHelper.ReadValue(op.Hive, op.View, op.Path, op.Name) is byte[] b
                    ? Convert.ToBase64String(b) : "";
            case OpKind.RegDelete:
                return RegHelper.ValueExists(op.Hive, op.View, op.Path, op.Name) ? "<present>" : "";
            default:
                return "";
        }
    }

    private static string? PriorRaw(OperationPriorState? p)
    {
        if (p is null) return null;
        return p.Kind switch
        {
            OpKind.TaskEnable => p.PriorTaskEnabled ? "enabled" : "disabled",
            OpKind.PowerScheme => p.PriorData,
            _ => p.Existed ? p.PriorData : ""
        };
    }
}
