using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F4 — Message-Signalled Interrupts (Hone <c>oPu4x2Ef</c>). Spec §3.4 D-MSI.
///
/// Swiftwave creates the <c>MessageSignaledInterruptProperties</c> key where a supported class
/// device hasn't yet declared MSI — a driver that hasn't registered this key simply falls back
/// to line-based interrupts; it doesn't break. Devices are filtered by class (Display,
/// StorageController, Net), WMI health, and a hard-deny of known-broken Intel Serial IO IDs.
///
/// State is journal-only (C3/C4).
/// </summary>
public sealed class MsiOptimization : Optimization
{
    private const string MsiSub = @"Interrupt Management\MessageSignaledInterruptProperties";

    private List<PlannedDevice> _plan = new();
    private List<string> _skipped = new();
    private string? _planReason;
    private string _snapshot = "{}";

    public override string Id => "v3-msi";
    public override string Name => "Optimize Message Signal Interrupts";
    public override string Description =>
        "Enables MSI on eligible GPU, NVMe and NIC devices (Display, StorageController, Net classes). " +
        "Creates the MessageSignaledInterruptProperties key on devices that don't already have one; " +
        "the MessageNumberLimit value is recorded before removal and restored on rollback. " +
        "Requires a restart; a device that reports an error after the write is restored immediately.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Advanced;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    public override Detection Detect()
    {
        if (PnpDeviceService.IsVirtualMachine())
            return new Detection(OptState.Unsupported, "Virtual machine — device interrupt tuning is disabled.", false, "E-VM");

        var live = Journal.LatestCommitted(Id);
        if (live is not null)
        {
            var current = live.Targets.Select(t => (t.Target, ReadTarget(t.Target))).ToList();
            return V3State.Evaluate(Id, current,
                "Written and accepted; devices apply MSI at next start (V2, restart required).",
                "No journal record.");
        }

        BuildPlan();
        if (_planReason is not null)
            return new Detection(OptState.Unsupported, ReasonText(_planReason), false, _planReason);
        return new Detection(OptState.NeedsAttention, "Not applied (no journal transaction).", true, "");
    }

    protected override string CapturePrevious()
    {
        BuildPlan();
        var snap = new Snapshot
        {
            ProfileHash = ProfileHash(),
            Devices = _plan.Select(d => new DeviceSnapshot
            {
                PnpId = d.PnpId,
                Name = d.Name,
                Primary = d.Primary,
                KeyExisted = d.MsiKeyExisted,
                MsiSupported = d.PriorMsi,
                MessageNumberLimit = d.PriorMessageNumberLimit
            }).ToList()
        };
        _snapshot = JsonSerializer.Serialize(snap);
        return _snapshot;
    }

    protected override bool ApplyChange(out string detail)
    {
        if (_plan.Count == 0) { detail = _planReason ?? "E-NO-DEVICE"; return false; }
        if (!string.Equals(ProfileHash(), CurrentSnapshotHash(), StringComparison.OrdinalIgnoreCase))
        {
            detail = "E-PROFILE-CHANGED — PCI inventory changed since planning.";
            return false;
        }

        var failures = new List<string>();
        foreach (var d in _plan)
        {
            try
            {
                string path = d.EnumRoot + @"\" + MsiSub;
                // CreateSubKey creates the full path including the parent keys; existing driver
                // keys are reused as-is (the creation is a no-op).
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                           .CreateSubKey(path, writable: true))
                {
                    if (key is null) { failures.Add($"{d.Name}: registry access denied"); continue; }
                    key.SetValue("MSISupported", 1, RegistryValueKind.DWord);

                    if (d.Primary && d.PriorMessageNumberLimit is not null)
                    {
                        // Recorded in CapturePrevious; safe to remove only for the primary GPU.
                        try { key.DeleteValue("MessageNumberLimit", throwOnMissingValue: false); } catch { }
                    }
                }

                var health = PnpDeviceService.QueryOne(d.PnpId);
                if (health is not null && !health.Healthy)
                {
                    RestoreDevice(d);
                    failures.Add($"{d.Name}: E-DEV-CONFIG-ERROR after write, restored");
                }
            }
            catch (Exception ex)
            {
                if (!d.MsiKeyExisted) { try { DeleteKey(d.EnumRoot + @"\" + MsiSub); } catch { } }
                failures.Add($"{d.Name}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            detail = "E-PARTIAL-FAILURE — " + string.Join("; ", failures);
            return false;
        }

        var targets = new List<JournalTarget>();
        foreach (var d in _plan)
        {
            targets.Add(new JournalTarget
            {
                Target = TargetKey(d, "MSISupported"), Kind = "DWord",
                Prior = d.PriorMsi?.ToString(), New = "1"
            });
            if (d.Primary && d.PriorMessageNumberLimit is not null)
                targets.Add(new JournalTarget
                {
                    Target = TargetKey(d, "MessageNumberLimit"), Kind = "DWord",
                    Prior = d.PriorMessageNumberLimit.ToString(), New = ""
                });
        }
        Journal.Commit(Id, targets);

        string skip = _skipped.Count > 0 ? " Skipped: " + string.Join("; ", _skipped) : "";
        detail = $"MSI enabled on {_plan.Count} device(s).{skip}";
        return true;
    }

    public override Detection DetectAfterApply()
    {
        foreach (var d in _plan)
        {
            string path = d.EnumRoot + @"\" + MsiSub;
            if (ReadDword(path, "MSISupported") != 1)
                return new Detection(OptState.NeedsAttention, $"{d.Name}: MSISupported did not persist.", true, "");
            if (d.Primary && d.PriorMessageNumberLimit is not null && ValueExists(path, "MessageNumberLimit"))
                return new Detection(OptState.NeedsAttention, $"{d.Name}: MessageNumberLimit still present.", true, "");

            var health = PnpDeviceService.QueryOne(d.PnpId);
            if (health is not null && !health.Healthy)
                return new Detection(OptState.NeedsAttention,
                    $"{d.Name}: device reports {health.Status} (ConfigManagerErrorCode {health.ConfigError}).",
                    true, "E-DEV-CONFIG-ERROR");
        }
        return new Detection(OptState.Optimized,
            "Written and accepted; devices apply MSI at next start (V2, restart required).", true, "");
    }

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
                string path = @"SYSTEM\CurrentControlSet\Enum\" + d.PnpId.Replace('/', '\\') + @"\" + MsiSub;
                bool keyExists = KeyExists(path);

                if (keyExists)
                {
                    using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                        .OpenSubKey(path, writable: true);
                    if (key is null) { failures.Add(d.Name + ": key absent"); continue; }

                    if (d.MsiSupported is int msi) key.SetValue("MSISupported", msi, RegistryValueKind.DWord);
                    else { try { key.DeleteValue("MSISupported", false); } catch { } }

                    if (d.MessageNumberLimit is int mn) key.SetValue("MessageNumberLimit", mn, RegistryValueKind.DWord);
                    else { try { key.DeleteValue("MessageNumberLimit", false); } catch { } }
                }

                // If Swiftwave created the key for this device, delete it entirely.
                if (!d.KeyExisted && KeyExists(path)) DeleteKey(path);
            }
            catch (Exception ex) { failures.Add(d.Name + ": " + ex.Message); }
        }

        Journal.MarkRolledBack(Id, failures.Count > 0);
        detail = failures.Count == 0 ? "Restored recorded MSI values." : "E-PARTIAL-FAILURE — " + string.Join("; ", failures);
        return failures.Count == 0;
    }

    private void BuildPlan()
    {
        _plan = new List<PlannedDevice>();
        _skipped = new List<string>();
        _planReason = null;

        var devices = PnpDeviceService.QueryAll();
        bool primaryAssigned = false;
        foreach (var cls in new[] { PnpDeviceClass.Display, PnpDeviceClass.StorageController, PnpDeviceClass.Net })
        {
            var device = devices.FirstOrDefault(d => d.Class == cls);
            if (device is null) { _skipped.Add($"{cls}: E-NO-DEVICE"); continue; }
            if (device.Class == PnpDeviceClass.System ||
                PnpDeviceService.IsHardDenied(device.VendorDevice))
            {
                _skipped.Add($"{device.Name}: E-DEV-SYSTEM-CLASS");
                continue;
            }
            if (!device.Healthy) { _skipped.Add($"{device.Name}: E-DEV-CONFIG-ERROR"); continue; }

            string path = device.EnumRoot + @"\" + MsiSub;
            bool priorKeyExisted = KeyExists(path);

            _plan.Add(new PlannedDevice
            {
                PnpId = device.PnpId,
                Name = device.Name,
                Primary = !primaryAssigned,
                MsiKeyExisted = priorKeyExisted,
                PriorMsi = priorKeyExisted ? ReadDword(path, "MSISupported") : null,
                PriorMessageNumberLimit = priorKeyExisted ? ReadDword(path, "MessageNumberLimit") : null
            });
            primaryAssigned = true;
        }

        if (_plan.Count == 0)
            _planReason = _skipped.FirstOrDefault()?.Contains("E-NO-DEVICE") == true
                ? "E-NO-DEVICE"
                : _skipped.FirstOrDefault() ?? "E-NO-DEVICE";
    }

    private static string TargetKey(PlannedDevice d, string name) =>
        "REG:HKLM\\" + d.EnumRoot + @"\" + MsiSub + "\\" + name;

    private static string? ReadTarget(string target)
    {
        const string prefix = "REG:HKLM\\";
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        string rest = target[prefix.Length..];
        int split = rest.LastIndexOf('\\');
        if (split <= 0) return null;
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(rest[..split]);
        string name = rest[(split + 1)..];
        if (key is null || !key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return null;
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return key.GetValueKind(name) == RegistryValueKind.DWord ? Convert.ToInt32(value).ToString() : value?.ToString();
    }

    private static bool KeyExists(string path) =>
        RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path) is not null;

    private static void DeleteKey(string path)
    {
        try
        {
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static bool ValueExists(string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key is not null && key.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }

    private static int? ReadDword(string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : null;
    }

    private static void RestoreDevice(PlannedDevice d)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(d.EnumRoot + @"\" + MsiSub, writable: true);
            if (key is null) return;
            if (d.PriorMsi is int msi) key.SetValue("MSISupported", msi, RegistryValueKind.DWord);
            else key.DeleteValue("MSISupported", false);
            if (d.PriorMessageNumberLimit is int mn) key.SetValue("MessageNumberLimit", mn, RegistryValueKind.DWord);
        }
        catch { }
        if (!d.MsiKeyExisted) DeleteKey(d.EnumRoot + @"\" + MsiSub);
    }

    private string ProfileHash()
    {
        var sb = new StringBuilder();
        foreach (var d in _plan) sb.Append(d.PnpId).Append('|');
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
        "E-NO-DEVICE" => "No eligible GPU, NVMe or NIC was found.",
        _ => "Unsupported: " + code
    };

    private sealed class PlannedDevice
    {
        public string PnpId { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Primary { get; init; }
        public bool MsiKeyExisted { get; init; }
        public int? PriorMsi { get; init; }
        public int? PriorMessageNumberLimit { get; init; }
        public string EnumRoot => @"SYSTEM\CurrentControlSet\Enum\" + PnpId.Replace('/', '\\');
    }

    private sealed class Snapshot
    {
        public string ProfileHash { get; set; } = "";
        public List<DeviceSnapshot> Devices { get; set; } = new();
    }

    private sealed class DeviceSnapshot
    {
        public string PnpId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Primary { get; set; }
        public bool KeyExisted { get; set; }
        public int? MsiSupported { get; set; }
        public int? MessageNumberLimit { get; set; }
    }
}
