using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F2 — "Optimize Storage &amp; Memory" / service-host consolidation (Hone <c>3WwEZo0M</c>).
/// Verdict: READY WITH MODIFICATIONS.
///
/// Gates (§5 F2): <c>LogicalProcessors &gt;= 4</c> and physical RAM ≥ 8 GiB. Below either
/// threshold the feature degrades to L1 — only <c>SvcHostSplitThresholdInKB</c> is written,
/// and no per-service <c>SvcHostSplitDisable</c> value is touched.
///
/// Shipped: threshold 0xFFFFFFFF; per-service SvcHostSplitDisable=1 for services that actually
/// exist and are hosted by <c>svchost.exe</c>; NTFS 8dot3 / last-access / memory-usage / scrub.
/// Excluded: NtfsMftZoneReservation (25 % reservation — opt-in, default off),
/// NtfsAllowExtendedCharacterIn8dot3Name (dead once 8dot3 is off), raw ReserveManager values
/// (the supported path is DISM /Set-ReservedStorageState), and Hone's fixed 49-service list
/// (third-party services are never touched).
/// </summary>
public sealed class StorageMemoryOptimization : MultiOpOptimization
{
    private const string ControlPath = @"SYSTEM\CurrentControlSet\Control";
    private const string FsPath = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    private static readonly string[] UserServiceBaseNames =
    {
        "AarSvc", "CDPUserSvc", "WpnUserService", "OneSyncSvc", "cbdhsvc", "CaptureService",
        "BluetoothUserService", "webthreatdefusersvc", "UnistoreSvc", "UdkUserSvc",
        "P9RdrService", "DevicesFlowUserSvc", "DeviceAssociationBrokerSvc", "DevicePickerUserSvc",
        "PrintWorkflowUserSvc", "MessagingService", "NPSMSvc", "CredentialEnrollmentManagerUserSvc",
        "PenService", "ConsentUxUserSvc", "CloudBackupRestoreSvc", "BcastDVRUserService",
        "UserDataSvc"
    };

    private List<Operation>? _ops;
    private bool _l1Only;

    public override string Id => "v3-storage-memory";
    public override string Name => "Optimize Storage & Memory";
    public override string Description =>
        "Raises the svchost consolidation threshold so more background services share hosts, and on " +
        "machines with 4+ logical processors and 8+ GiB RAM also marks existing svchost-hosted services " +
        "for consolidation (below either threshold only the threshold value is changed). NTFS 8.3 name " +
        "generation and last-access updates are disabled and the NTFS cache enlarged. CAUTION: " +
        "consolidation widens fault domains; restart required.";
    public override string Category => "Storage";
    public override Safety Safety => Safety.Advanced;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    protected override IReadOnlyList<Operation> Operations => _ops ??= BuildOps();

    private static bool GateOpen(out string reason)
    {
        int lps = Environment.ProcessorCount;
        long ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        const long eightGiB = 8L * 1024 * 1024 * 1024;
        if (lps < 4) { reason = $"L1 — {lps} logical processor(s) (<4): threshold only."; return false; }
        if (ram < eightGiB) { reason = $"L1 — {ram / (1024 * 1024 * 1024)} GiB RAM (<8): threshold only."; return false; }
        reason = "";
        return true;
    }

    private List<Operation> BuildOps()
    {
        var ops = new List<Operation>
        {
            new(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                ControlPath, "SvcHostSplitThresholdInKB", IntValue: unchecked((int)0xFFFFFFFF))
        };

        bool gate = GateOpen(out _);
        _l1Only = !gate;
        if (gate)
        {
            foreach (var name in ExistingSvchostHostedServices()) 
                ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                    $@"{ServicesPath}\{name}", "SvcHostSplitDisable", IntValue: 1));
        }

        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            FsPath, "NtfsDisable8dot3NameCreation", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            FsPath, "NtfsDisableLastAccessUpdate", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            FsPath, "NtfsMemoryUsage", IntValue: 2));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            FsPath, "ScrubMode", IntValue: 1));

        return ops;
    }

    /// <summary>Existing services whose ImagePath is svchost.exe and whose name matches a per-user base name.</summary>
    private static IEnumerable<string> ExistingSvchostHostedServices()
    {
        var result = new List<string>();
        try
        {
            using var services = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(ServicesPath);
            if (services is null) return result;
            foreach (var name in services.GetSubKeyNames())
            {
                if (!UserServiceBaseNames.Any(b => name.StartsWith(b, StringComparison.Ordinal))) continue;
                using var key = services.OpenSubKey(name);
                if (key?.GetValue("ImagePath") is not string image) continue;
                if (!image.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(name);
            }
        }
        catch { }
        return result;
    }

    public override Detection Detect()
    {
        if (_ops is null) _ops = BuildOps();
        return JournalDetection(_l1Only
            ? "Service-host threshold raised (L1: threshold only on this hardware)."
            : "Service hosts consolidated and NTFS metadata settings adjusted.");
    }
}
