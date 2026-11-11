using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// V3 Optimization — "Disable Dynamic Interrupt Steering".
/// Verdict from Final-Report §3.6 G: **READY**.
///
/// One DWORD `InterruptSteeringFlags=0` under the kernel feature-flags key.
/// Requires reboot to take effect.
/// Honest label: kernel interrupt steering disabled — no measurable effect on most systems.
/// (Hone's claim that this is "per power plan" was not observed in the capture — it's just one DWORD.)
/// </summary>
public sealed class DynamicInterruptSteeringOptimization : MultiOpOptimization
{
    private const string KernelPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

    public override string Id => "v3-dynamic-interrupt-steering";
    public override string Name => "Disable Dynamic Interrupt Steering";
    public override string Description =>
        "Disables the kernel's runtime interrupt re-targeting. A single boot-time DWORD. " +
        "Effect is hardware-dependent and may be a no-op; it is a low-risk, trivially-reversible change.";
    public override string Category => "Gaming";
    public override Safety Safety => Safety.Moderate;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => true;

    protected override IReadOnlyList<Operation> Operations { get; } = new List<Operation>
    {
        new(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            KernelPath, "InterruptSteeringFlags", IntValue: 0)
    };

    public override Detection Detect()
    {
        if (Environment.OSVersion.Version.Build < 19041)
            return new Detection(OptState.Unsupported,
                "Windows 10 2004 (build 19041) or newer is required.", false, "E-OS-UNSUPPORTED");
        if (PnpDeviceService.IsVirtualMachine())
            return new Detection(OptState.Unsupported,
                "Virtual machine — kernel interrupt steering is not available.", false, "E-VM");

        return JournalDetection("Interrupt steering disabled (InterruptSteeringFlags = 0).");
    }
}
