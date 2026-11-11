using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F9 — Maximum Performance Power Mode (Hone <c>k6ad89bg</c>). Spec §3.9 D-PWR, extended with
/// the desktop/laptop split mandated by the correction pass.
///
/// Desktop: imports <c>Swiftwave-Ultimate-Performance.pow</c> with <c>powercfg /import</c>
/// (no GUID argument → Windows allocates a fresh GUID, never Hone's 44444444-…4452), renames it
/// to "Swiftwave Ultimate Performance", verifies all 27 AC/DC rows and activates it.
///
/// Laptop: duplicates the canonical Windows High Performance scheme, renames the copy to
/// "Swiftwave High Performance" and activates it; the original Microsoft scheme is never edited.
///
/// Rollback restores the previously active scheme and deletes only the Swiftwave-owned scheme
/// (Hone defect D-1). If the user changed the active scheme after apply, it is left untouched.
/// </summary>
public sealed class MaxPerformancePowerModeOptimization : Optimization
{
    private const string SchemesKey = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    public const string UltimateName = "Swiftwave Ultimate Performance";
    public const string HighName = "Swiftwave High Performance";

    private enum FormFactor { Unknown, Desktop, Laptop, Ambiguous }

    private string? _priorActive;
    private string? _ourGuid;
    private FormFactor _form = FormFactor.Unknown;
    private string _snapshot = "{}";

    public override string Id => "v3-max-performance-power";
    public override string Name => "Enable Maximum Performance Power Mode";
    public override string Description =>
        "Applies Swiftwave's optimal power plan tailored for your PC.";
    public override string Category => "Power";
    public override Safety Safety => Safety.Moderate;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => false;

    // ------------------------------------------------------------------ detect

    public override Detection Detect()
    {
        if (PnpDeviceService.IsVirtualMachine())
            return new Detection(OptState.Unsupported, "Virtual machine — power-plan replacement is disabled.", false, "E-VM");
        if (PnpDeviceService.IsServer())
            return new Detection(OptState.Unsupported, "Windows Server is not supported by this optimization.", false, "E-OS-SERVER");
        if (Environment.OSVersion.Version.Build < 19041)
            return new Detection(OptState.Unsupported, "Windows 10 2004 (build 19041) or newer is required.", false, "E-OS-UNSUPPORTED");

        var form = ClassifyFormFactor();
        if (form is FormFactor.Unknown or FormFactor.Ambiguous)
            return new Detection(OptState.Unsupported,
                "Desktop/laptop classification is ambiguous or unavailable — no power plan is created or activated.",
                false, "E-CLASS-AMBIGUOUS");

        var live = Journal.LatestCommitted(Id);
        if (live is not null)
        {
            bool desktop = form == FormFactor.Desktop;
            return V3State.Evaluate(Id, CurrentTargets(desktop),
                $"Swiftwave scheme active ({ShortGuid(PowerSchemeService.ReadActiveScheme())}).",
                "No journal record.");
        }

        return new Detection(OptState.NeedsAttention,
            $"{form} machine — Swiftwave power scheme not applied.", true, "");
    }

    // ------------------------------------------------------------------ capture

    protected override string CapturePrevious()
    {
        _form = ClassifyFormFactor();
        _priorActive = PowerSchemeService.ReadActiveScheme();
        var snap = new Snapshot
        {
            PriorActive = _priorActive,
            Form = _form.ToString(),
            ProfileHash = ProfileHash()
        };
        _snapshot = JsonSerializer.Serialize(snap);
        return _snapshot;
    }

    // ------------------------------------------------------------------ apply

    protected override bool ApplyChange(out string detail)
    {
        detail = "";
        if (_form is not (FormFactor.Desktop or FormFactor.Laptop))
        {
            detail = "E-CLASS-AMBIGUOUS";
            return false;
        }
        if (!string.Equals(ProfileHash(), CurrentSnapshotHash(), StringComparison.OrdinalIgnoreCase))
        {
            detail = "E-PROFILE-CHANGED — form factor or OS build changed since capture.";
            return false;
        }

        var before = PowerSchemeService.ListSchemes().Select(s => s.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_form == FormFactor.Desktop)
            {
                // The plan ships as an embedded resource; extract to a unique temp file so
                // powercfg can read it, then always remove it again (success or failure).
                string? pow = PowerPlanAsset.ExtractToTemp(out var extractError);
                if (pow is null)
                {
                    detail = extractError ?? "The bundled power plan could not be extracted.";
                    return false;
                }

                try
                {
                    var imported = PowerSchemeService.ImportScheme(pow);
                    var after = PowerSchemeService.ListSchemes().Select(s => s.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    after.ExceptWith(before);
                    _ourGuid = after.FirstOrDefault() ?? imported;
                    if (_ourGuid is null) { detail = "powercfg /import did not produce a new scheme."; return false; }

                    if (!PowerSchemeService.RenameScheme(_ourGuid, UltimateName))
                    {
                        Cleanup(_ourGuid);
                        detail = "The imported scheme could not be renamed.";
                        return false;
                    }

                    var mismatch = FirstMismatch(_ourGuid);
                    if (mismatch is not null)
                    {
                        Cleanup(_ourGuid);
                        detail = "Imported plan failed content verification: " + mismatch;
                        return false;
                    }
                }
                finally
                {
                    PowerPlanAsset.TryDelete(pow);
                }
            }
            else
            {
                var dup = PowerSchemeService.DuplicateScheme(PowerSchemeService.HighPerformanceGuid);
                var after = PowerSchemeService.ListSchemes().Select(s => s.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                after.ExceptWith(before);
                _ourGuid = after.FirstOrDefault() ?? dup;
                if (_ourGuid is null) { detail = "High Performance could not be duplicated."; return false; }

                if (!PowerSchemeService.RenameScheme(_ourGuid, HighName))
                {
                    Cleanup(_ourGuid);
                    detail = "The duplicated scheme could not be renamed.";
                    return false;
                }
            }

            if (!PowerSchemeService.Activate(_ourGuid))
            {
                Cleanup(_ourGuid);
                detail = "The new scheme could not be activated.";
                return false;
            }
        }
        catch (Exception ex)
        {
            if (_ourGuid is not null) Cleanup(_ourGuid);
            detail = ex.Message;
            return false;
        }

        var targets = new List<JournalTarget>
        {
            new() { Target = "POWER:ACTIVE", Kind = "SchemeGuid", Prior = _priorActive, New = _ourGuid! }
        };
        if (_form == FormFactor.Desktop)
        {
            foreach (var row in Rows)
            {
                targets.Add(new JournalTarget { Target = Key(row, "AC"), Kind = "DWord", Prior = null, New = row.Ac.ToString() });
                targets.Add(new JournalTarget { Target = Key(row, "DC"), Kind = "DWord", Prior = null, New = row.Dc.ToString() });
            }
        }
        Journal.Commit(Id, targets);

        detail = _form == FormFactor.Desktop
            ? $"Imported and activated '{UltimateName}' ({ShortGuid(_ourGuid)}); 27 settings verified."
            : $"Duplicated High Performance as '{HighName}' ({ShortGuid(_ourGuid)}) and activated it.";
        return true;
    }

    public override Detection DetectAfterApply()
    {
        if (_ourGuid is null) return new Detection(OptState.NeedsAttention, "No scheme GUID was produced.", true, "");
        var active = PowerSchemeService.ReadActiveScheme();
        if (!string.Equals(active, _ourGuid, StringComparison.OrdinalIgnoreCase))
            return new Detection(OptState.NeedsAttention, $"Active scheme is {ShortGuid(active)}, expected {ShortGuid(_ourGuid)}.", true, "");

        if (_form == FormFactor.Desktop)
        {
            var mismatch = FirstMismatch(_ourGuid);
            if (mismatch is not null)
                return new Detection(OptState.NeedsAttention, "Content verification failed: " + mismatch, true, "");
            return new Detection(OptState.Optimized, $"'{UltimateName}' active; all 27 AC/DC rows verified.", true, "");
        }

        var name = PowerSchemeService.GetFriendlyName(_ourGuid);
        if (!string.Equals(name, HighName, StringComparison.OrdinalIgnoreCase))
            return new Detection(OptState.NeedsAttention, $"Scheme name is '{name}', expected '{HighName}'.", true, "");
        return new Detection(OptState.Optimized, $"'{HighName}' active.", true, "");
    }

    // ------------------------------------------------------------------ revert

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        Snapshot? snap;
        try { snap = JsonSerializer.Deserialize<Snapshot>(previousState); }
        catch (Exception ex) { detail = "Snapshot unreadable: " + ex.Message; return false; }
        if (snap is null) { detail = "Snapshot empty."; return false; }

        string? ourGuid = _ourGuid
            ?? Journal.LatestCommitted(Id)?.Targets.FirstOrDefault(t => t.Target == "POWER:ACTIVE")?.New;
        var current = PowerSchemeService.ReadActiveScheme();

        bool restored = false;
        string note;
        if (ourGuid is not null && string.Equals(current, ourGuid, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(snap.PriorActive))
                restored = PowerSchemeService.Activate(snap.PriorActive);
            note = restored
                ? $"Restored previously active scheme {ShortGuid(snap.PriorActive)}."
                : "E-PARTIAL-FAILURE — could not reactivate the previous scheme.";
        }
        else if (!string.Equals(current, snap.PriorActive, StringComparison.OrdinalIgnoreCase))
        {
            // User changed the active scheme after apply: never overwrite their choice (C2-4).
            note = $"Active scheme was changed externally to {ShortGuid(current)}; left untouched.";
            restored = true;
        }
        else
        {
            note = "Previous scheme already active.";
            restored = true;
        }

        if (ourGuid is not null && PowerSchemeService.Exists(ourGuid) &&
            !string.Equals(PowerSchemeService.ReadActiveScheme(), ourGuid, StringComparison.OrdinalIgnoreCase))
        {
            PowerSchemeService.DeleteScheme(ourGuid);
        }

        Journal.MarkRolledBack(Id, !restored);
        detail = note;
        return restored;
    }

    // ------------------------------------------------------------------ classification

    private FormFactor ClassifyFormFactor()
    {
        bool hasBattery = PnpDeviceService.HasBattery(out bool batteryKnown);
        if (!batteryKnown) return FormFactor.Unknown;

        var chassis = PnpDeviceService.GetChassisTypes(out bool chassisKnown);
        bool? chassisMobile = chassisKnown
            ? chassis.Any(t => t is 8 or 9 or 10 or 14 or 30 or 31 or 32)
              || (chassis.Length > 0 && chassis.All(t => t is 8 or 9 or 10 or 14 or 30 or 31 or 32))
            : null;

        if (!chassisKnown) return hasBattery ? FormFactor.Laptop : FormFactor.Desktop;

        if (hasBattery && chassisMobile == false) return FormFactor.Ambiguous;
        if (!hasBattery && chassisMobile == true) return FormFactor.Ambiguous;
        return hasBattery ? FormFactor.Laptop : FormFactor.Desktop;
    }

    // ------------------------------------------------------------------ verification

    private static string? FirstMismatch(string guid)
    {
        foreach (var row in Rows)
        {
            string path = SettingPath(guid, row);
            var ac = ReadDword(path, "ACSettingIndex");
            var dc = ReadDword(path, "DCSettingIndex");
            if (ac != row.Ac) return $"{row.Setting} AC expected {row.Ac}, observed {ac?.ToString() ?? "absent"}";
            if (dc != row.Dc) return $"{row.Setting} DC expected {row.Dc}, observed {dc?.ToString() ?? "absent"}";
        }
        return null;
    }

    private List<(string Target, string? Actual)> CurrentTargets(bool desktop)
    {
        var list = new List<(string, string?)> { ("POWER:ACTIVE", PowerSchemeService.ReadActiveScheme()) };
        if (!desktop) return list;

        var liveActive = PowerSchemeService.ReadActiveScheme();
        foreach (var row in Rows)
        {
            string? guid = liveActive;
            if (string.IsNullOrEmpty(guid)) { list.Add((Key(row, "AC"), null)); list.Add((Key(row, "DC"), null)); continue; }
            string path = SettingPath(guid, row);
            list.Add((Key(row, "AC"), ReadDword(path, "ACSettingIndex")?.ToString()));
            list.Add((Key(row, "DC"), ReadDword(path, "DCSettingIndex")?.ToString()));
        }
        return list;
    }

    private void Cleanup(string guid)
    {
        try
        {
            if (!string.IsNullOrEmpty(_priorActive)) PowerSchemeService.Activate(_priorActive);
            if (PowerSchemeService.Exists(guid)) PowerSchemeService.DeleteScheme(guid);
        }
        catch { }
        _ourGuid = null;
    }

    // ------------------------------------------------------------------ helpers

    private static string ShortGuid(string? g) => string.IsNullOrEmpty(g) || g.Length < 8 ? "unknown" : g[..8] + "…";

    private static int? ReadDword(string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static string SettingPath(string guid, Row row) =>
        row.AtRoot ? $@"{SchemesKey}\{guid}\{row.Setting}" : $@"{SchemesKey}\{guid}\{row.Subgroup}\{row.Setting}";

    private static string Key(Row row, string acdc) =>
        $"POWER:{(row.AtRoot ? "ROOT" : row.Subgroup)}|{row.Setting}|{acdc}";

    private string ProfileHash()
    {
        string s = $"{_form}|{Environment.OSVersion.Version.Build}|{(PnpDeviceService.HasBattery(out _) ? "battery" : "ac")}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }

    private string CurrentSnapshotHash()
    {
        try
        {
            using var doc = JsonDocument.Parse(_snapshot);
            return doc.RootElement.GetProperty("ProfileHash").GetString() ?? "";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ models

    private sealed class Snapshot
    {
        public string? PriorActive { get; set; }
        public string Form { get; set; } = "";
        public string ProfileHash { get; set; } = "";
    }

    private readonly record struct Row(string Subgroup, string Setting, int Ac, int Dc, bool AtRoot = false);

    private const string Disk = "0012ee47-9041-4b5d-9b77-535fba8b1442";
    private const string Processor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string Display = "7516b95f-f776-4464-8c53-06167f40cc99";
    private const string Usb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string IdleResiliency = "2e601130-5351-4d9d-8e04-252966bad054";

    /// <summary>The 27 rows verified against the reconstructed V5 plan (comparison CSV).</summary>
    private static readonly Row[] Rows =
    {
        new(Disk, "6738e2c4-e8a5-4a42-b16a-e040e769756e", 3600, 600),
        new(Disk, "d3d55efd-c1ff-424e-9dc3-441be7833010", 4000, 1000),
        new(Disk, "d639518a-e56d-4345-8af2-b9f32fb26109", 500, 100),
        new("", "245d8541-3943-4422-b025-13a784f679b7", 1, 2, AtRoot: true),
        new(Usb, "0853a681-27c8-4100-a2fd-82013e970683", 200, 50),
        new(Usb, "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", 0, 0),
        new(Usb, "d4e98f31-5ffe-4ce1-be31-1b38b384c009", 0, 0),
        new(IdleResiliency, "c42b79aa-aa3a-484b-a98f-2cf32aa90a28", 65000, 65000),
        new(Processor, "0cc5b647-c1df-4637-891a-dec35c318583", 100, 100),
        new(Processor, "0cc5b647-c1df-4637-891a-dec35c318584", 100, 100),
        new(Processor, "12a0ab44-fe28-4fa9-b3bd-4b64f44960a7", 10, 30),
        new(Processor, "1facfc65-a930-4bc5-9f38-504ec097bbc0", 100, 50),
        new(Processor, "2ddd5a84-5a71-437e-912a-db0b8c788732", 1, 7),
        new(Processor, "447235c7-6a8d-4cc0-8e24-9eaf70b96e2b", 1, 1),
        new(Processor, "447235c7-6a8d-4cc0-8e24-9eaf70b96e2c", 2, 1),
        new(Processor, "4d2b0152-7d5c-498b-88e2-34345392a2c5", 5000, 5000),
        new(Processor, "616cdaa5-695e-4545-97ad-97dc2d1bdd89", 25, 0),
        new(Processor, "619b7505-003b-4e82-b7a6-4dd29c300972", 100, 50),
        new(Processor, "7f2492b6-60b1-45e5-ae55-773f8cd5caec", 5, 3),
        new(Processor, "bae08b81-2d5e-4688-ad6a-13243356654b", 2, 5),
        new(Processor, "be337238-0d82-4146-a960-4f3749d470c7", 2, 1),
        new(Processor, "c4581c31-89ab-4597-8e2b-9c9cab440e6b", 200000, 200000),
        new(Processor, "d8edeb9b-95cf-4f95-a73c-b061973693c9", 2, 1),
        new(Display, "17aaa29b-8b43-4b94-aafe-35f64daaf1ee", 885, 60),
        new(Display, "3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e", 2700, 90),
        new(Display, "aded5e82-b909-4619-9949-f5d71dac0bcb", 100, 100),
        new(Display, "f1fbfde2-a960-4165-9f88-50667911ce96", 50, 40)
    };
}
