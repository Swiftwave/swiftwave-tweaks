using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F3 — Device Interrupt Affinity (Hone <c>RqbxUufM</c>). Spec §3.3 D-AFF.
///
/// Every gate is applied per machine and per device; a rejected device is skipped individually
/// with its reason code, and the machine-level gates (E-VM, E-TOPO-DEGENERATE, E-GRP-GT64,
/// E-HYBRID-AMBIGUOUS, E-PROFILE-CHANGED) produce zero writes.
///
/// The mask is calculated by <see cref="CpuTopologyService.DeriveAffinity"/>:
///   - P-cores only when hybrid classification is calibrated (E-1);
///   - never CPU 0;
///   - at most two distinct physical cores, at most two bits;
///   - single processor group and &lt;= 64 LPs only (E-GRP-GT64).
///
/// State is journal-only: the feature reports Optimized only when a committed transaction exists
/// and every recorded value still matches (C3/C4).
/// </summary>
public sealed class DeviceAffinityOptimization : Optimization
{
    private const string AffinitySub = @"Interrupt Management\Affinity Policy";

    private List<PlannedDevice> _plan = new();
    private List<string> _skipped = new();
    private CpuTopologyResult? _topology;
    private string? _planReason;
    private string _snapshot = "{}";

    public override string Id => "v3-device-affinity";
    public override string Name => "Optimize Device Affinities";
    public override string Description =>
        "Pins GPU, NVMe and primary NIC interrupts to a topology-derived set of performance cores. " +
        "Creates the Affinity Policy key on devices that don't already have one; preserves any " +
        "pre-existing values that are unrelated to interrupt affinities. " +
        "Requires a restart for the driver to re-read the policy.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Advanced;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    // ------------------------------------------------------------------ detect

    public override Detection Detect()
    {
        if (PnpDeviceService.IsVirtualMachine())
            return new Detection(OptState.Unsupported, "Virtual machine — interrupt tuning is disabled.", false, "E-VM");

        var live = Journal.LatestCommitted(Id);
        if (live is not null)
        {
            var current = live.Targets.Select(t => (t.Target, ReadTarget(t.Target))).ToList();
            return V3State.Evaluate(Id, current,
                "Written; devices re-read the policy at next start (restart required).",
                "No journal record.");
        }

        BuildPlan();
        if (_planReason is not null)
            return new Detection(OptState.Unsupported, ReasonText(_planReason), false, _planReason);

        return new Detection(OptState.NeedsAttention, "Not applied (no journal transaction).", true, "");
    }

    // ------------------------------------------------------------------ capture

    protected override string CapturePrevious()
    {
        BuildPlan();
        var snap = new Snapshot
        {
            ProfileHash = ProfileHash(),
            MaskB64 = Convert.ToBase64String(_plan.FirstOrDefault()?.Mask ?? Array.Empty<byte>()),
            Cpus = _plan.FirstOrDefault()?.Cpus.ToList() ?? new List<int>(),
            Devices = _plan.Select(d => new DeviceSnapshot
            {
                PnpId = d.PnpId,
                Name = d.Name,
                Class = d.Class.ToString(),
                Primary = d.Primary,
                KeyExisted = d.PriorKeyExisted,
                DevicePolicy = d.PriorPolicy,
                DevicePriority = d.PriorPriority,
                AssignmentSetOverride = d.PriorMaskB64
            }).ToList()
        };
        _snapshot = JsonSerializer.Serialize(snap);
        return _snapshot;
    }

    // ------------------------------------------------------------------ apply

    protected override bool ApplyChange(out string detail)
    {
        var topology = CpuTopologyService.Discover();
        string? reason = null;
        byte[]? mask = null;
        bool derived = topology.Ok &&
                       CpuTopologyService.DeriveAffinity(topology, out mask, out _, out reason, out _);
        if (!derived)
        {
            detail = reason ?? topology.Reason ?? "E-CPU-UNKNOWN";
            return false;
        }
        if (_plan.Count == 0 || mask is null) { detail = _planReason ?? "E-NO-DEVICE"; return false; }
        if (!string.Equals(ProfileHash(), CurrentSnapshotHash(), StringComparison.OrdinalIgnoreCase))
        {
            detail = "E-PROFILE-CHANGED — hardware/topology changed since planning; re-run detection.";
            return false;
        }

        var failures = new List<string>();
        foreach (var d in _plan)
        {
            try
            {
                string keyPath = d.EnumRoot + @"\" + AffinitySub;
                // CreateSubKey creates the full path; if the driver already registered the key,
                // this is a no-op, and ReadTarget/PriorPolicy below reflect the pre-existing state.
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                           .CreateSubKey(keyPath, writable: true))
                {
                    if (key is null) { failures.Add($"{d.Name}: registry access denied"); continue; }
                    key.SetValue("DevicePolicy", 4, RegistryValueKind.DWord);
                    if (d.Primary) key.SetValue("DevicePriority", 3, RegistryValueKind.DWord);
                    key.SetValue("AssignmentSetOverride", mask, RegistryValueKind.Binary);
                }

                // Mandatory post-write health assertion.
                var health = PnpDeviceService.QueryOne(d.PnpId);
                if (health is not null && !health.Healthy)
                {
                    RestoreDevice(d);
                    failures.Add($"{d.Name}: E-DEV-CONFIG-ERROR after write, restored");
                }
            }
            catch (Exception ex)
            {
                // If we created the key on a partial failure, delete it so no stray key is left
                // behind for a device that didn't previously have one.
                if (!d.PriorKeyExisted) { try { DeleteKey(d.EnumRoot + @"\" + AffinitySub); } catch { } }
                failures.Add($"{d.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            detail = "E-PARTIAL-FAILURE — " + string.Join("; ", failures);
            return false;
        }

        Journal.Commit(Id, _plan.SelectMany(d => new[]
        {
            new JournalTarget { Target = TargetKey(d, "DevicePolicy"), Kind = "DWord", Prior = Enc(d.PriorPolicy), New = "4" },
            new JournalTarget { Target = TargetKey(d, "DevicePriority"), Kind = "DWord", Prior = Enc(d.PriorPriority), New = "3" },
            new JournalTarget { Target = TargetKey(d, "AssignmentSetOverride"), Kind = "Binary", Prior = d.PriorMaskB64, New = Convert.ToBase64String(mask) }
        }));

        detail = Ledger();
        return true;
    }

    private string Ledger()
    {
        string cpus = _plan.Count > 0 ? string.Join(",", _plan[0].Cpus) : "";
        string skip = _skipped.Count > 0 ? " Skipped: " + string.Join("; ", _skipped) : "";
        return $"Applied to {_plan.Count} device(s), CPU set [{cpus}].{skip}";
    }

    // ------------------------------------------------------------------ verify

    public override Detection DetectAfterApply()
    {
        foreach (var d in _plan)
        {
            var policy = ReadDword(d.EnumRoot + @"\" + AffinitySub, "DevicePolicy");
            var mask = ReadBinary(d.EnumRoot + @"\" + AffinitySub, "AssignmentSetOverride");
            if (policy != 4) return new Detection(OptState.NeedsAttention, $"{d.Name}: DevicePolicy not 4.", true, "");
            if (mask is null || !mask.SequenceEqual(d.Mask))
                return new Detection(OptState.NeedsAttention, $"{d.Name}: affinity mask did not persist.", true, "");
            if (d.Primary && ReadDword(d.EnumRoot + @"\" + AffinitySub, "DevicePriority") != 3)
                return new Detection(OptState.NeedsAttention, $"{d.Name}: DevicePriority not 3.", true, "");

            var health = PnpDeviceService.QueryOne(d.PnpId);
            if (health is not null && !health.Healthy)
                return new Detection(OptState.NeedsAttention, $"{d.Name}: device reports {health.Status} (ConfigManagerErrorCode {health.ConfigError}).", true, "E-DEV-CONFIG-ERROR");
        }
        return new Detection(OptState.Optimized,
            "Written and accepted; devices apply the policy at next start (V2, restart required).", true, "");
    }

    // ------------------------------------------------------------------ revert

    public override bool Revert(string previousState, out string detail)
    {
        detail = "";
        Snapshot? snap;
        try { snap = JsonSerializer.Deserialize<Snapshot>(previousState); }
        catch (Exception ex) { detail = "Snapshot unreadable: " + ex.Message; return false; }
        if (snap is null) { detail = "Snapshot empty."; return false; }

        var failures = new List<string>();
        foreach (var d in snap.Devices)
        {
            try
            {
                string keyPath = @"SYSTEM\CurrentControlSet\Enum\" + d.PnpId.Replace('/', '\\') + @"\" + AffinitySub;
                bool keyExists = KeyExists(keyPath);

                if (keyExists)
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(keyPath, writable: true);
                    if (key is null) { failures.Add(d.Name + ": key absent"); continue; }

                    RestoreValue(key, "DevicePolicy", d.DevicePolicy?.ToString());
                    RestoreValue(key, "DevicePriority", d.DevicePriority?.ToString());
                    RestoreValue(key, "AssignmentSetOverride", d.AssignmentSetOverride);
                }

                // If Swiftwave created the key and the device didn't have one before, delete it.
                if (!d.KeyExisted && KeyExists(keyPath)) DeleteKey(keyPath);
            }
            catch (Exception ex) { failures.Add(d.Name + ": " + ex.Message); }
        }

        Journal.MarkRolledBack(Id, failures.Count > 0);
        detail = failures.Count == 0 ? "Restored recorded affinity values." : "E-PARTIAL-FAILURE — " + string.Join("; ", failures);
        return failures.Count == 0;
    }

    private static void RestoreValue(RegistryKey key, string name, string? encoded)
    {
        if (encoded is null) { try { key.DeleteValue(name, false); } catch { } return; }
        var kind = key.GetValueKind(name);
        if (kind == RegistryValueKind.Binary || name == "AssignmentSetOverride")
            key.SetValue(name, Convert.FromBase64String(encoded), RegistryValueKind.Binary);
        else
            key.SetValue(name, int.Parse(encoded), RegistryValueKind.DWord);
    }

    // ------------------------------------------------------------------ planning

    private void BuildPlan()
    {
        _plan = new List<PlannedDevice>();
        _skipped = new List<string>();
        _planReason = null;
        _topology = CpuTopologyService.Discover();
        if (!_topology.Ok) { _planReason = _topology.Reason ?? "E-CPU-UNKNOWN"; return; }

        if (!CpuTopologyService.DeriveAffinity(_topology, out var mask, out var cpus, out var reason, out _))
        {
            _planReason = reason;
            return;
        }
        if (mask is null) { _planReason = "E-TOPO-DEGENERATE"; return; }

        var bootDisplays = PnpDeviceService.BootDisplayIds();
        var devices = PnpDeviceService.QueryAll();
        var wanted = new[] { PnpDeviceClass.Display, PnpDeviceClass.StorageController, PnpDeviceClass.Net };
        bool primaryAssigned = false;

        foreach (var cls in wanted)
        {
            var device = devices.FirstOrDefault(d => d.Class == cls);
            if (device is null) { _skipped.Add($"{cls}: E-NO-DEVICE"); continue; }
            if (PnpDeviceService.IsHardDenied(device.VendorDevice))
            {
                _skipped.Add($"{device.Name}: E-DEV-SYSTEM-CLASS (hard-deny vendor/device)");
                continue;
            }
            if (!device.Healthy)
            {
                _skipped.Add($"{device.Name}: E-DEV-CONFIG-ERROR");
                continue;
            }
            if (cls == PnpDeviceClass.Display && bootDisplays.Contains(device.PnpId))
            {
                _skipped.Add($"{device.Name}: E-DEV-BOOT-DISPLAY (boot display requires explicit confirmation)");
                continue;
            }

            string keyPath = device.EnumRoot + @"\" + AffinitySub;
            bool priorKeyExisted = KeyExists(keyPath);

            _plan.Add(new PlannedDevice
            {
                PnpId = device.PnpId,
                Name = device.Name,
                Class = cls,
                Primary = !primaryAssigned,
                PriorKeyExisted = priorKeyExisted,
                PriorPolicy = ReadDword(device.EnumRoot + @"\" + AffinitySub, "DevicePolicy"),
                PriorPriority = ReadDword(device.EnumRoot + @"\" + AffinitySub, "DevicePriority"),
                PriorMaskB64 = ReadBinary(device.EnumRoot + @"\" + AffinitySub, "AssignmentSetOverride") is byte[] b
                    ? Convert.ToBase64String(b) : null,
                Mask = mask,
                Cpus = cpus.ToList()
            });
            primaryAssigned = true;
        }

        if (_plan.Count == 0)
            _planReason = _skipped.FirstOrDefault()?.Contains("E-NO-DEVICE") == true
                ? "E-NO-DEVICE"
                : _skipped.FirstOrDefault() ?? "E-NO-DEVICE";
    }

    // ------------------------------------------------------------------ helpers

    private static string TargetKey(PlannedDevice d, string name) =>
        "REG:HKLM\\" + d.EnumRoot + @"\" + AffinitySub + "\\" + name;

    private static string? ReadTarget(string target)
    {
        const string prefix = "REG:HKLM\\";
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = target[prefix.Length..];
        int split = rest.LastIndexOf('\\');
        if (split <= 0) return null;
        string path = rest[..split];
        string name = rest[(split + 1)..];
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        if (key is null || !key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return null;
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return kind switch
        {
            RegistryValueKind.DWord => Convert.ToInt32(value).ToString(),
            RegistryValueKind.Binary => Convert.ToBase64String((byte[])value!),
            _ => value?.ToString()
        };
    }

    private static bool KeyExists(string path)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key is not null;
    }

    private static void DeleteKey(string path)
    {
        try
        {
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static int? ReadDword(string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static byte[]? ReadBinary(string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key?.GetValue(name) is byte[] v ? v : null;
    }

    private static string Enc(int? v) => v?.ToString() ?? "";

    private static void RestoreDevice(PlannedDevice d)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(d.EnumRoot + @"\" + AffinitySub, writable: true);
            if (key is null) return;
            RestoreValue(key, "DevicePolicy", d.PriorPolicy?.ToString());
            RestoreValue(key, "DevicePriority", d.PriorPriority?.ToString());
            RestoreValue(key, "AssignmentSetOverride", d.PriorMaskB64);
        }
        catch { }
        // If Swiftwave created the key for this device and the apply failed, delete the key too.
        if (!d.PriorKeyExisted) DeleteKey(d.EnumRoot + @"\" + AffinitySub);
    }

    private string ProfileHash()
    {
        var topo = _topology ?? CpuTopologyService.Discover();
        var sb = new StringBuilder();
        sb.Append(topo.Description).Append('|').Append(topo.LogicalProcessors).Append('|').Append(topo.Groups);
        foreach (var d in _plan) sb.Append('|').Append(d.PnpId);
        if (_plan.Count > 0) sb.Append('|').Append(Convert.ToBase64String(_plan[0].Mask));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
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

    private static string ReasonText(string code) => code switch
    {
        "E-GRP-GT64" => "More than one processor group or more than 64 logical processors — the 8-byte affinity mask would address the wrong CPUs, so nothing is written.",
        "E-HYBRID-AMBIGUOUS" => "Performance/efficiency core classification could not be calibrated from CPUID and the Windows CPU set — nothing is written.",
        "E-TOPO-DEGENERATE" => "Fewer than 2 physical cores or fewer than 4 logical processors — pinning would reduce throughput.",
        "E-NO-DEVICE" => "No eligible GPU, NVMe or NIC was found.",
        _ => "Unsupported: " + code
    };

    // ------------------------------------------------------------------ models

    private sealed class PlannedDevice
    {
        public string PnpId { get; init; } = "";
        public string Name { get; init; } = "";
        public PnpDeviceClass Class { get; init; }
        public bool Primary { get; init; }
        public bool PriorKeyExisted { get; init; }
        public int? PriorPolicy { get; init; }
        public int? PriorPriority { get; init; }
        public string? PriorMaskB64 { get; init; }
        public byte[] Mask { get; init; } = Array.Empty<byte>();
        public IReadOnlyList<int> Cpus { get; init; } = Array.Empty<int>();
        public string EnumRoot => @"SYSTEM\CurrentControlSet\Enum\" + PnpId.Replace('/', '\\');
    }

    private sealed class Snapshot
    {
        public string ProfileHash { get; set; } = "";
        public string MaskB64 { get; set; } = "";
        public List<int> Cpus { get; set; } = new();
        public List<DeviceSnapshot> Devices { get; set; } = new();
    }

    private sealed class DeviceSnapshot
    {
        public string PnpId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Class { get; set; } = "";
        public bool Primary { get; set; }
        public bool KeyExisted { get; set; }
        public int? DevicePolicy { get; set; }
        public int? DevicePriority { get; set; }
        public string? AssignmentSetOverride { get; set; }
    }
}
