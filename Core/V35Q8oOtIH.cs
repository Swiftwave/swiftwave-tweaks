using System.Security.Principal;
using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F1 — "Optimize FPS &amp; Input Lag" (Hone <c>5Q8oOtIH</c>). Verdict: READY WITH MODIFICATIONS.
///
/// Shipped subset (Final-Report §3.1 G):
///  • BCD: delete <c>useplatformclock</c> / <c>useplatformtick</c> — only when present, so the delete
///    can never fail on a machine where the value was already absent.
///  • Shutdown timeouts: <c>WaitToKillServiceTimeout</c>, <c>WaitToKillAppTimeout</c> = "1500" (REG_SZ).
///  • Per-user desktop timeouts: HungAppTimeout / WaitToKillAppTimeout / LowLevelHooksTimeout.
///    <c>AutoEndTasks</c> is deliberately dropped — it is a data-loss vector.
///  • DirectX swap-effect upgrade: SwapEffectUpgradeEnable + SwapEffectUpgradeCache.
///  • Explorer StartupDelayInMSec / WaitforIdleState, Dsh IsPrelaunchEnabled, input IsInputAppPreloadEnabled.
///
/// Excluded: <c>disabledynamictick</c>, the 10 undocumented Session Manager\kernel tunables,
/// RawMouse* ×4, NoLazyMode, AllowDeepCStates, VRR optimisation 0, AllowAutoGameMode,
/// GlobalUserDisabled + appprivacy policy, and Hone's SetTimerResolution task (we do not ship
/// their helper binary). The five services Hone coerced to Automatic are already Automatic on
/// stock Windows — treated as verify-only, so there is no operation to roll back.
/// </summary>
public sealed class FpsInputLagOptimization : MultiOpOptimization
{
    private const string Control = @"SYSTEM\CurrentControlSet\Control";
    private const string Serialize = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
    private const string Dsh = @"Software\Microsoft\Windows\CurrentVersion\Dsh";
    private const string InputKey = @"Software\Microsoft\input";
    private const string DxSettings = @"Software\Microsoft\directx\graphicssettings";
    private const string Desktop = @"Control Panel\Desktop";

    private List<Operation>? _ops;

    public override string Id => "v3-fps-input-lag";
    public override string Name => "General Windows FPS & Input Delay Optimizations";
    public override string Description =>
        "Removes a forced BCD platform clock/tick when present, enables DirectX swap-effect upgrade, " +
        "reduces Explorer shell startup delay, disables app prelaunch, and shortens Windows shutdown " +
        "timeouts. Undocumented kernel scheduler tunables and Hone's timer-resolution helper are NOT included. " +
        "BCD changes need a reboot to take effect.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Moderate;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    protected override IReadOnlyList<Operation> Operations => _ops ??= BuildOps();

    private static List<Operation> BuildOps()
    {
        var ops = new List<Operation>();

        // BCD values are only deleted when they are actually present. bcdedit /deletevalue on an
        // absent value returns exit code 1, which would abort the whole apply.
        string bcd = BcdEnum();
        if (bcd.Contains("useplatformclock", StringComparison.OrdinalIgnoreCase))
            ops.Add(new Operation(OpKind.BcdEdit, Command: "/deletevalue {current} useplatformclock"));
        if (bcd.Contains("useplatformtick", StringComparison.OrdinalIgnoreCase))
            ops.Add(new Operation(OpKind.BcdEdit, Command: "/deletevalue {current} useplatformtick"));

        ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
            Control, "WaitToKillServiceTimeout", StringValue: "1500"));
        ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
            Control, "WaitToKillAppTimeout", StringValue: "1500"));

        ops.Add(new Operation(OpKind.RegString, RegistryHive.CurrentUser, RegistryView.Default,
            Desktop, "HungAppTimeout", StringValue: "1000"));
        ops.Add(new Operation(OpKind.RegString, RegistryHive.CurrentUser, RegistryView.Default,
            Desktop, "WaitToKillAppTimeout", StringValue: "1500"));
        ops.Add(new Operation(OpKind.RegString, RegistryHive.CurrentUser, RegistryView.Default,
            Desktop, "LowLevelHooksTimeout", StringValue: "1000"));

        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            DxSettings, "SwapEffectUpgradeEnable", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            DxSettings, "SwapEffectUpgradeCache", IntValue: 1));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            Serialize, "StartupDelayInMSec", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            Serialize, "WaitforIdleState", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            Dsh, "IsPrelaunchEnabled", IntValue: 0));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default,
            InputKey, "IsInputAppPreloadEnabled", IntValue: 0));

        return ops;
    }

    private static string BcdEnum()
    {
        var r = ProcessRunner.RunCapture("bcdedit.exe", "/enum {current}");
        return r is not null && r.Value.ExitCode == 0 ? r.Value.Output : "";
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public override Detection Detect()
    {
        if (PnpDeviceService.IsServer())
            return new Detection(OptState.Unsupported,
                "Server SKU — timer/latency tuning is not applied.", false, "E-OS-SERVER");

        string bcd = BcdEnum();
        if (bcd.Length == 0)
        {
            // bcdedit /enum needs elevation. A non-elevated session is a permission limitation,
            // not an unsupported machine — say so instead of presenting the card as Unsupported.
            if (!IsElevated())
                return new Detection(OptState.Unknown,
                    "Administrator permission is required to read the boot configuration. " +
                    "Relaunch Swiftwave Tweaks as administrator to detect this optimization.",
                    false, "");
            return new Detection(OptState.Unsupported,
                "Boot configuration could not be read (bcdedit unavailable or blocked).", false, "E-OS-UNSUPPORTED");
        }

        var state = JournalDetection("FPS & input-lag profile applied (BCD platform clock/tick absent, shell/input timeouts set).");
        if (state.State != OptState.Optimized) return state;

        bool clockPresent = bcd.Contains("useplatformclock", StringComparison.OrdinalIgnoreCase);
        bool tickPresent = bcd.Contains("useplatformtick", StringComparison.OrdinalIgnoreCase);
        if (clockPresent || tickPresent)
            return new Detection(OptState.NeedsAttention,
                "BCD still forces a platform clock/tick — re-apply to remove it.", true, "E-FOREIGN-EDIT");

        return state;
    }

    protected override string? RestoreCommand(Operation op)
    {
        // Hone's BCD deletes were only applied when the value existed; the prior value was absent,
        // so the correct inverse is "leave it absent". Nothing to replay.
        return null;
    }
}
