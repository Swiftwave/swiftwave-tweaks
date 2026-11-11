using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// Consolidated debloat feature: merges the former "Reduce Windows suggestions" card with
/// Hone's Disable Windows Services (PE9bgwi0) and Reduce Windows Background Activity (9C0X9LiC).
///
/// Ownership:
///   If the legacy <c>windows-suggestions</c> rollback record exists, its originally captured
///   ContentDeliveryManager values are adopted as this feature's prior state, so rollback restores
///   the true pre-optimization values rather than the already-disabled ones. The legacy record is
///   removed after a successful apply, transferring ownership to this feature.
///
/// Deliberately excluded: SharedAccess (hotspot/ICS), upnphost (DLNA), RemoteRegistry, TrkWks,
/// WMPNetworkSvc, icssvc — these change user-facing functionality and are gated behind explicit
/// confirmation in the specification. WSearch is excluded from this merged card (it degrades
/// search) and remains a separate future toggle.
/// </summary>
public sealed class DebloatOptimization : MultiOpOptimization
{
    private const string LegacyId = "windows-suggestions";
    private const string CdmPath = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private const string WerKey = @"SOFTWARE\Microsoft\Windows\Windows Error Reporting";
    private const string WerPoliciesKey = @"SOFTWARE\Policies\Microsoft\Windows\windows error reporting";
    private const string AppCompatKey = @"SOFTWARE\Policies\Microsoft\Windows\AppCompat";
    private const string DeviceInstallKey = @"SOFTWARE\Policies\Microsoft\Windows\deviceinstall\settings";

    private const string NduNote =
        "Ndu (Windows network data usage driver) is disabled; network usage statistics in Settings will stop updating.";

    private static readonly string[] SuggestionKeys =
    {
        "SubscribedContent-338389Enabled",
        "SubscribedContent-338388Enabled",
        "SubscribedContent-310093Enabled",
        "SubscribedContent-338393Enabled",
        "SubscribedContent-353694Enabled",
        "SubscribedContent-353696Enabled",
        "SoftLandingEnabled",
        "SystemPaneSuggestionsEnabled",
        "SilentInstalledAppsEnabled"
    };

    private static readonly string[] DisabledServices =
    {
        "WalletService", "SEMgrSvc", "PhoneSvc", "AxInstSV", "Ndu",
        "PcaSvc", "WerSvc", "wercplsupport"
    };

    private static readonly string[] TaskPaths =
    {
        @"\Microsoft\Windows\Windows Error Reporting\QueueReporting",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
        @"\Microsoft\Windows\Feedback\Siuf\DmClient",
        @"\Microsoft\Windows\Feedback\Siuf\DmClientOnScenarioDownload",
        @"\Microsoft\Windows\Application Experience\PcaPatchDbTask",
        @"\Microsoft\Windows\Application Experience\StartupAppTask",
        @"\Microsoft\Windows\Application Experience\MareBackup",
        @"\Microsoft\Windows\ApplicationData\CleanupTemporaryState",
        @"\Microsoft\Windows\ApplicationData\DsSvcCleanup",
        @"\Microsoft\Windows\DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector",
        @"\Microsoft\Windows\DiskFootprint\Diagnostics",
        @"\Microsoft\Windows\File Classification Infrastructure\Property Definition Sync",
        @"\Microsoft\Windows\Location\WindowsActionDialog",
        @"\Microsoft\Windows\Maintenance\WinSAT",
        @"\Microsoft\Windows\Management\Provisioning\Logon",
        @"\Microsoft\Windows\Maps\MapsToastTask",
        @"\Microsoft\Windows\Maps\MapsUpdateTask",
        @"\Microsoft\Windows\NlaSvc\WiFiTask",
        @"\Microsoft\Windows\Offline Files\Background Synchronization",
        @"\Microsoft\Windows\Offline Files\Logon Synchronization",
        @"\Microsoft\Windows\Ras\MobilityManager",
        @"\Microsoft\Windows\Shell\FamilySafetyMonitor",
        @"\Microsoft\Windows\SpacePort\SpaceAgentTask",
        @"\Microsoft\Windows\SpacePort\SpaceManagerTask",
        @"\Microsoft\Windows\Speech\SpeechModelDownloadTask",
        @"\Microsoft\Windows\User Profile Service\HiveUploadTask",
        @"\Microsoft\Windows\WCM\WiFiTask",
        @"\Microsoft\Windows\Windows Filtering Platform\BfeOnServiceStartTypeChange",
        @"\Microsoft\Windows\Windows Media Sharing\UpdateLibrary",
        @"\Microsoft\Windows\Work Folders\Work Folders Logon Synchronization",
        @"\Microsoft\Windows\Work Folders\Work Folders Maintenance Work",
        @"\Microsoft\Windows\Workplace Join\Automatic-Device-Join"
    };

    private Dictionary<string, string?>? _legacyPriors;

    public override string Id => "debloat";
    public override string Name => "Debloat Windows & Reduce Background Activity";
    public override string Description =>
        "Disables Windows suggestion surfaces, non-essential services (Wallet, NFC, Phone Link, ActiveX " +
        "installer, network data usage, compatibility assistant, error reporting) and 33 low-value scheduled " +
        "tasks, and turns off crash reporting. " +
        NduNote + " " +
        "Mobile hotspot, DLNA/UPnP and Windows Search are NOT touched. Every value and task state is recorded " +
        "and restored on rollback.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Advanced;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    protected override IReadOnlyList<Operation> Operations { get; } = BuildOps();

    private static IReadOnlyList<Operation> BuildOps()
    {
        var ops = new List<Operation>();

        foreach (var name in SuggestionKeys)
            ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
                CdmPath, name, IntValue: 0));

        foreach (var service in DisabledServices)
            ops.Add(new Operation(OpKind.ServiceStart, RegistryHive.LocalMachine, RegistryView.Registry64,
                $@"{ServicesPath}\{service}", "Start", IntValue: 4));

        foreach (var path in TaskPaths)
            ops.Add(new Operation(OpKind.TaskEnable, TaskPath: path, BoolValue: false));

        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerKey, "Disabled", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerKey, "DontSendAdditionalData", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerKey, "DontShowUI", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerKey, "LoggingDisabled", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\Assert Filtering Policy", "ReportAndContinue", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\Windows Error Reporting\Consent", "DefaultConsent", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerPoliciesKey, "Disabled", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, WerPoliciesKey, "DontSendAdditionalData", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, AppCompatKey, "DisableEngine", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, AppCompatKey, "DisablePCA", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, AppCompatKey, "AllowTelemetry", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, DeviceInstallKey, "DisableSendGenericDriverNotFoundToWER", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64, DeviceInstallKey, "DisableSendRequestAdditionalSoftwareToWER", IntValue: 1));

        return ops;
    }

    public override Detection Detect()
    {
        ScheduledTaskService.PrimeCache(TaskPaths);
        return JournalDetection(
            "Debloated: suggestion surfaces off, 8 services disabled, 33 tasks disabled, crash reporting off.");
    }

    protected override OperationPriorState? AdoptPrior(Operation op)
    {
        if (op.Kind != OpKind.RegDword || op.Hive != RegistryHive.CurrentUser) return null;
        var legacy = LegacyPriors();
        if (legacy is null || !legacy.TryGetValue(op.Name, out var prior)) return null;

        // Legacy "v" is "0"/"1" or null (value absent at original capture time).
        return new OperationPriorState(op.Kind, prior is not null, "DWord", prior, 0, false, false, "");
    }

    protected override void OnApplied()
    {
        // Ownership transfer: the merged feature now owns the CDM keys.
        if (_legacyPriors is not null)
            RollbackStore.Remove(LegacyId);
    }

    private Dictionary<string, string?>? LegacyPriors()
    {
        if (_legacyPriors is not null) return _legacyPriors;
        var item = RollbackStore.Read().FirstOrDefault(x => x.Id == LegacyId);
        if (item is null) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(item.PreviousState);
            if (!doc.RootElement.TryGetProperty("settings", out var arr)) return null;
            var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in arr.EnumerateArray())
            {
                string name = el.GetProperty("k").GetString() ?? "";
                if (name.Length == 0) continue;
                var v = el.GetProperty("v");
                map[name] = v.ValueKind == System.Text.Json.JsonValueKind.Null ? null : v.GetString();
            }
            _legacyPriors = map;
            return map;
        }
        catch { return null; }
    }
}
