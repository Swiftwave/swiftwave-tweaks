using Microsoft.Win32;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

/// <summary>
/// F5 — "Optimize Network Performance" (Hone <c>XLSEdxVW</c>). Verdict: READY WITH MODIFICATIONS.
///
/// Shipped: documented netsh globals (rss/taskoffload/netdma/dca/uro/uso/loopback-large-mtu/isatap),
/// a competence-probed congestion-provider change (bbr2 only when this build reports it), the nine
/// documented <c>Tcpip\Parameters</c> values, the four <c>Tcpip6</c> offload values plus
/// <c>DisabledComponents=1</c> (tunnel-only IPv6 off — native IPv6 intact), the three DNS negative-cache
/// values, the <c>qos "Do not use NLA"</c> string, the three <c>LanmanServer\Parameters</c> values, and
/// the per-adapter NIC power-save block.
///
/// NIC keys are resolved per adapter via <c>DriverDesc</c>/<c>NetCfgInstanceId</c>; virtual and
/// Hyper-V adapters are skipped. The numeric instance index (Hone's fixed <c>0001</c>) is never assumed.
///
/// Excluded: <c>initialRto=2000</c> (doubles first retransmit), the <c>UpperBind</c> rewrite
/// (destructive and not reliably reversible), <c>NetbiosOptions=2</c> + <c>lmhosts=Disabled</c>
/// (gated behind an explicit legacy-LAN opt-in), <c>EnableAutoDoh</c> (changes DNS for every app),
/// <c>Psched\TimerResolution</c> (needs validation), BITS <c>SvcHostSplitDisable=0</c> (C2-1 conflict),
/// and the ~452-entry QoS DSCP flood.
/// </summary>
public sealed class NetworkPerformanceOptimization : MultiOpOptimization
{
    private const string TcpipParams = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private const string Tcpip6Params = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";
    private const string DnsCache = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters";
    private const string QosKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\qos";
    private const string Lanman = @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters";
    private const string NicClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly (string Command, string Inverse)[] NetshOps =
    {
        ("int isatap set state disabled", "int isatap set state default"),
        ("int tcp set global rss=enabled", "int tcp set global rss=default"),
        ("int tcp set global nonsackrttresiliency=disabled", "int tcp set global nonsackrttresiliency=default"),
        ("int tcp set global netdma=disabled", "int tcp set global netdma=default"),
        ("int tcp set global dca=disabled", "int tcp set global dca=default"),
        ("int ip set global taskoffload=enabled", "int ip set global taskoffload=default"),
        ("int ipv4 set global loopbacklargemtu=disabled", "int ipv4 set global loopbacklargemtu=enabled"),
        ("int ipv6 set global loopbacklargemtu=disabled", "int ipv6 set global loopbacklargemtu=enabled"),
        ("int udp set global uro=disabled", "int udp set global uro=default"),
        ("int udp set global uso=enabled", "int udp set global uso=default"),
    };

    private List<Operation>? _ops;
    private string? _bbrNote;

    public override string Id => "v3-network-performance";
    public override string Name => "Optimize Network Performance";
    public override string Description =>
        "Turns on RSS and task offload, disables ISATAP/netdma/DCA/URO, enables USO, shortens TCP " +
        "time-wait, and turns off per-adapter NIC power saving. The congestion provider is only set to " +
        "bbr2 when this Windows build reports support. Hone's contradictory initialRto=2000 and the " +
        "destructive UpperBind rewrite are excluded.";
    public override string Category => "Network";
    public override Safety Safety => Safety.Moderate;
    public override bool RequiresAdmin => true;
    public override bool RequiresRestart => false;

    protected override IReadOnlyList<Operation> Operations => _ops ??= BuildOps();

    private List<Operation> BuildOps()
    {
        var ops = new List<Operation>();

        foreach (var (cmd, _) in NetshOps)
            ops.Add(new Operation(OpKind.NetshCommand, Command: cmd));

        // Congestion provider is a probe, never an assumption.
        bool bbr2 = Bbr2Supported();
        _bbrNote = bbr2 ? "bbr2 supported" : "E-BBR2-UNSUPPORTED — provider left at current value";
        if (bbr2)
            ops.Add(new Operation(OpKind.NetshCommand,
                Command: "int tcp set supplemental template=internet congestionprovider=bbr2"));

        var tcpip = new (string Name, int Value)[]
        {
            ("DisableTaskOffload", 0), ("EnableTCPChimney", 0), ("EnableTCPA", 0), ("EnableDCA", 0),
            ("TcpMaxConnectRetransmissions", 2), ("TcpTimedWaitDelay", 32), ("MultihopSets", 0x0F),
            ("SackOpts", 1), ("Tcp1323Opts", 3)
        };
        foreach (var (name, value) in tcpip)
            ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                TcpipParams, name, IntValue: value));

        var tcpip6 = new (string Name, int Value)[]
        {
            ("DisableTaskOffload", 0), ("EnableTCPChimney", 0), ("EnableTCPA", 0), ("EnableDCA", 0),
            ("DisabledComponents", 1)
        };
        foreach (var (name, value) in tcpip6)
            ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                Tcpip6Params, name, IntValue: value));

        foreach (var name in new[] { "NegativeCacheTime", "NegativeSOACacheTime", "NetFailureCacheTime" })
            ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                DnsCache, name, IntValue: 0));

        ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
            QosKey, "Do not use NLA", StringValue: "1"));

        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            Lanman, "IRPStackSize", IntValue: 30));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            Lanman, "SizReqBuf", IntValue: 0x4410));
        ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
            Lanman, "Size", IntValue: 3));

        foreach (var (keyPath, desc) in PhysicalNetworkAdapters())
        {
            if (string.IsNullOrWhiteSpace(desc)) continue;
            ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
                keyPath, "*EEE", StringValue: "0"));
            ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
                keyPath, "EnableGreenEthernet", StringValue: "0"));
            ops.Add(new Operation(OpKind.RegString, RegistryHive.LocalMachine, RegistryView.Registry64,
                keyPath, "MIMOPowerSaveMode", StringValue: "3"));
            ops.Add(new Operation(OpKind.RegDword, RegistryHive.LocalMachine, RegistryView.Registry64,
                keyPath, "PnPCapabilities", IntValue: 0x18));
        }

        return ops;
    }

    private static bool Bbr2Supported()
    {
        var r = ProcessRunner.RunCapture("netsh.exe", "int tcp show supplemental template=internet");
        return r is not null && r.Value.ExitCode == 0 &&
               r.Value.Output.Contains("bbr2", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Enumerates real (non-virtual) network adapters by DriverDesc, skipping ghost instances.</summary>
    private static IEnumerable<(string KeyPath, string Desc)> PhysicalNetworkAdapters()
    {
        var list = new List<(string, string)>();
        try
        {
            using var cls = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(NicClass);
            if (cls is null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                using var key = cls.OpenSubKey(sub);
                if (key is null) continue;
                if (key.GetValue("DriverDesc") is not string desc || desc.Length == 0) continue;
                if (key.GetValue("NetCfgInstanceId") is not string) continue;
                if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(($@"{NicClass}\{sub}", desc));
            }
        }
        catch { }
        return list;
    }

    public override Detection Detect()
    {
        if (PnpDeviceService.IsServer())
            return new Detection(OptState.Unsupported, "Server SKU — network stack profile is not applied.",
                false, "E-OS-SERVER");
        if (PnpDeviceService.IsVirtualMachine())
            return new Detection(OptState.Unsupported, "Virtual machine — network stack tuning is skipped.",
                false, "E-VM");

        var state = JournalDetection("Network stack profile applied (offloads, TCP timers, DNS cache, NIC power save).");
        if (state.State == OptState.Optimized && _bbrNote is not null && _bbrNote.StartsWith("E-BBR2", StringComparison.Ordinal))
            return new Detection(OptState.Optimized,
                state.CurrentState + " · " + _bbrNote, true, "E-BBR2-UNSUPPORTED");
        return state;
    }

    protected override string? RestoreCommand(Operation op)
    {
        if (op.Kind != OpKind.NetshCommand) return null;
        string inverse = op.Command.StartsWith("int tcp set supplemental", StringComparison.OrdinalIgnoreCase)
            ? "int tcp set supplemental template=internet congestionprovider=default"
            : NetshOps.FirstOrDefault(n => n.Command == op.Command).Inverse ?? "";
        if (inverse.Length == 0) return null;
        var (code, _) = RunCommand("netsh.exe", inverse);
        return code == 0 ? null : $"netsh inverse failed: {inverse}";
    }
}
