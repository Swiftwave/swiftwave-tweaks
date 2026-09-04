using System;
using System.Collections.Generic;
using System.Linq;
using SwiftwaveTweaks.Core;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// One energy/power-saving advanced property discovered on the active wired adapter.
/// These are the ONLY network settings this tool touches — link-level energy features
/// (Energy Efficient Ethernet, Green Ethernet, vendor Power Saving Mode) that can drop
/// the link into low-power states and introduce latency variance. RSS, interrupt
/// moderation, checksum/LSO offloads and flow control are deliberately never modified.
/// </summary>
public sealed record NetworkEnergySetting(string Keyword, string Label, int Value);

/// <summary>
/// A live reading of the active physical wired adapter and whichever energy/power-saving
/// advanced properties it actually exposes. <see cref="EnergySettings"/> is empty when the
/// adapter exposes none of the known energy keywords — callers must treat that as
/// Unsupported, not as a blank card.
/// </summary>
public sealed record NetworkAdapterReading(
    bool Present,
    string Name,
    string Description,
    string LinkSpeed,
    string DriverVersion,
    IReadOnlyList<NetworkEnergySetting> EnergySettings)
{
    public static readonly NetworkAdapterReading None = new(false, "", "", "", "", Array.Empty<NetworkEnergySetting>());

    /// <summary>True when the adapter exposes at least one manageable energy/power-saving control.</summary>
    public bool SupportsEnergyControl => EnergySettings.Count > 0;

    /// <summary>True when every exposed energy/power-saving control is disabled (value 0).</summary>
    public bool AllEnergyDisabled => EnergySettings.Count > 0 && EnergySettings.All(s => s.Value == 0);
}

/// <summary>
/// Reads and (via elevation) configures network-adapter energy/power-saving advanced
/// properties using the documented Windows NetAdapter PowerShell cmdlets
/// (Get-NetAdapter / Get-NetAdapterAdvancedProperty / Set-NetAdapterAdvancedProperty).
///
/// Scope is deliberately narrow and safe:
///   • Detection runs un-elevated and only reads.
///   • Apply/Revert target the energy/power-saving keywords below and nothing else.
///   • No RSS, interrupt moderation, checksum offload, LSO, flow-control, buffer, speed/
///     duplex, VLAN, MAC or Wake-on-LAN value is ever written.
///   • No undocumented registry edits are performed — all changes go through
///     Set-NetAdapterAdvancedProperty, the supported driver interface.
/// </summary>
public static class NetworkAdapterService
{
    /// <summary>
    /// Energy/power-saving advanced-property keywords recognised here, mapped to readable
    /// labels. Detection only reports a keyword when the adapter's driver actually exposes
    /// it, so non-Realtek / unsupported adapters surface nothing to disable.
    /// </summary>
    private static readonly (string Keyword, string Label)[] EnergyKeywords = new[]
    {
        ("*EEE",                   "Energy Efficient Ethernet (EEE)"),
        ("AdvancedEEE",            "Advanced EEE"),
        ("EnableGreenEthernet",    "Green Ethernet"),
        ("PowerSavingMode",        "Power Saving Mode"),
    };

    /// <summary>Reads the active wired adapter and its energy settings on a background thread. Returns None when no suitable adapter is found.</summary>
    public static Task<NetworkAdapterReading> ReadAsync() => Task.Run(Read);

    /// <summary>Reads the active wired adapter and its energy settings. Returns None when no suitable adapter is found.</summary>
    public static NetworkAdapterReading Read()
    {
        var script = @"
$ErrorActionPreference='SilentlyContinue'
$ad = Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.MediaType -eq '802.3' -and $_.HardwareInterface -eq $true -and $_.InterfaceDescription -notlike '*WAN Miniport*' -and $_.LinkSpeed -ne '0 bps' } | Select-Object -First 1
if ($null -eq $ad) { 'ADAPTER_FOUND=False'; return }
'ADAPTER_FOUND=True'
('ADAPTER_NAME=' + $ad.Name)
('ADAPTER_DESC=' + $ad.InterfaceDescription)
('LINKSPEED=' + $ad.LinkSpeed)
('DRIVER=' + $ad.DriverVersion)
$props = $ad | Get-NetAdapterAdvancedProperty
foreach ($k in @('*EEE','AdvancedEEE','EnableGreenEthernet','PowerSavingMode')) {
  $p = $props | Where-Object { $_.RegistryKeyword -eq $k } | Select-Object -First 1
  if ($null -ne $p) {
    $v = ($p.RegistryValue | Select-Object -First 1)
    'KW=' + $k + '=' + $v
  }
}
";
        var r = ProcessRunner.RunPowerShell(script);
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output))
        {
            SafeLog.Write($"Network read: PowerShell returned exit {r.ExitCode}.");
            return NetworkAdapterReading.None;
        }

        var lines = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                           .Select(l => l.Trim())
                           .ToList();
        if (!lines.Any(l => l.StartsWith("ADAPTER_FOUND=True", StringComparison.Ordinal)))
            return NetworkAdapterReading.None;

        string name = ValueOf(lines, "ADAPTER_NAME=");
        string desc = ValueOf(lines, "ADAPTER_DESC=");
        string link = ValueOf(lines, "LINKSPEED=");
        string driver = ValueOf(lines, "DRIVER=");

        var settings = new List<NetworkEnergySetting>();
        foreach (var (kw, label) in EnergyKeywords)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith("KW=" + kw + "=", StringComparison.Ordinal));
            if (line is null) continue;
            var raw = line.Substring(("KW=" + kw + "=").Length);
            if (int.TryParse(raw, out int val))
                settings.Add(new NetworkEnergySetting(kw, label, val));
        }

        SafeLog.Write($"Network adapter detected: {desc} ({link}); energy controls exposed: {settings.Count}.");
        return new NetworkAdapterReading(true, name, desc, link, driver, settings);
    }

    /// <summary>
    /// Disables every exposed energy/power-saving control (sets each present keyword to 0)
    /// using the documented Set-NetAdapterAdvancedProperty cmdlet (elevated). The caller is
    /// responsible for the independent post-read verification.
    /// </summary>
    public static bool TryDisableEnergySaving(string adapterDescription, IReadOnlyList<NetworkEnergySetting> targets, out string detail)
    {
        detail = "";
        var kwList = string.Join(",", targets.Select(t => "`'" + t.Keyword + "`'"));
        var script = $@"
$ErrorActionPreference='Stop'
$ad = Get-NetAdapter -InterfaceDescription '{adapterDescription}'
if ($null -eq $ad) {{ 'FAIL=adapter not found'; exit 1 }}
$kws = @({kwList})
foreach ($k in $kws) {{
  try {{
    $ad | Set-NetAdapterAdvancedProperty -RegistryKeyword $k -RegistryValue 0 -ErrorAction Stop
    'SET=OK:' + $k
  }} catch {{
    'SET=FAIL:' + $k + ':' + $_.Exception.Message
    exit 2
  }}
}}
";
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
        detail = r.Output.Trim();
        bool ok = r.ExitCode == 0 && detail.Contains("SET=OK:") && !detail.Contains("SET=FAIL:");
        SafeLog.Write($"Network energy-saving disable: {(ok ? "succeeded" : "failed")} :: {detail}");
        return ok;
    }

    /// <summary>
    /// Restores each energy/power-saving control to its exact previously-captured value
    /// using Set-NetAdapterAdvancedProperty (elevated). The caller performs post-read verification.
    /// </summary>
    public static bool TryRestoreEnergySaving(string adapterDescription, IReadOnlyList<NetworkEnergySetting> previous, out string detail)
    {
        detail = "";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$ad = Get-NetAdapter -InterfaceDescription '{adapterDescription}'");
        sb.AppendLine("if ($null -eq $ad) { 'FAIL=adapter not found'; exit 1 }");
        foreach (var s in previous)
        {
            // Keywords and values here come from the driver's own enumeration (keyword) and an
            // integer previously read from the same adapter — never free-form user input.
            sb.AppendLine($"try {{ $ad | Set-NetAdapterAdvancedProperty -RegistryKeyword '{s.Keyword}' -RegistryValue {s.Value} -ErrorAction Stop; 'SET=OK:{s.Keyword}=' + {s.Value} }} catch {{ 'SET=FAIL:{s.Keyword}:' + $_.Exception.Message; exit 2 }}");
        }
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(sb.ToString());
        detail = r.Output.Trim();
        bool ok = r.ExitCode == 0 && detail.Contains("SET=OK:") && !detail.Contains("SET=FAIL:");
        SafeLog.Write($"Network energy-saving restore: {(ok ? "succeeded" : "failed")} :: {detail}");
        return ok;
    }

    private static string ValueOf(IEnumerable<string> lines, string prefix)
        => lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?.Substring(prefix.Length).Trim() ?? "";
}
