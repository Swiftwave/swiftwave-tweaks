using System.Collections.Generic;
using System.Linq;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

public sealed record DiagnosticIssue(string Problem, string WhyItMatters, string RecommendedAction, string Mode, string Risk);

/// <summary>Central diagnostics pass built from detection data. Read-only. (spec 29)</summary>
public static class DiagnosticsEngine
{
    public static List<DiagnosticIssue> Run(SystemSnapshot s, NvidiaInfo nvidia)
    {
        var issues = new List<DiagnosticIssue>();

        // Memory running below its rated profile (spec 26)
        var slow = s.RamModules.FirstOrDefault(m => m.SpeedMts is int rated && rated > 0 && m.ConfiguredSpeedMts is int cfg && cfg < rated);
        if (slow is not null)
            issues.Add(new DiagnosticIssue(
                "Your memory may not be running at its configured performance profile.",
                $"A module reports {slow.ConfiguredSpeedMts ?? 0} MT/s but is rated for {slow.SpeedMts} MT/s. Memory bandwidth and latency can affect 1% lows.",
                "Enable EXPO/XMP in BIOS/UEFI. This cannot be done safely from Windows.",
                "Manual (BIOS)", "Low"));

        // Startup load
        int startupCount = StartupService.Enumerate().Count(e => e.Enabled);
        if (startupCount > 10)
            issues.Add(new DiagnosticIssue(
                $"{startupCount} startup applications are enabled.",
                "Too many startup programs increase boot time and can consume CPU, RAM and disk activity while gaming.",
                "Review the Startup page and disable non-critical entries.",
                "Automatic (Startup page)", "Low"));

        // Background processes
        if (s.ProcessCount > 180)
            issues.Add(new DiagnosticIssue(
                $"{s.ProcessCount} processes are running.",
                "A large number of background applications can affect CPU, RAM, disk and GPU/VRAM availability, and may reduce frametime consistency.",
                "Close or uninstall unused background applications; review overlays and updaters.",
                "Manual", "Low"));

        // Nearly full drives
        foreach (var d in s.Drives.Where(d => d.SizeGb > 20 && d.FreeGb / d.SizeGb < 0.10))
            issues.Add(new DiagnosticIssue(
                $"Drive {d.Letter} is nearly full ({d.FreeGb:N0} GB free of {d.SizeGb:N0} GB).",
                "SSDs slow down when nearly full and Windows needs headroom for updates and the page file.",
                "Run Safe Cleanup or move large files off the drive.",
                "Automatic (Cleanup page)", "Low"));

        // HDD present
        var hdd = s.Drives.FirstOrDefault(d => d.SizeGb > 100 && !d.BusKind.Contains("SSD") && !d.BusKind.Contains("NVMe"));
        if (hdd is not null)
            issues.Add(new DiagnosticIssue(
                $"{hdd.Model} appears to be a mechanical drive (bus: {hdd.BusKind}).",
                "Games installed on a mechanical drive show long load times and asset streaming hitches.",
                "Install games on the NVMe/SSD drive, or move them via your game launcher.",
                "Manual", "Low"));

        // Refresh rate
        foreach (var m in s.Monitors.Where(m => m.MaxHzAtCurrentRes > m.CurrentHz + 2))
            issues.Add(new DiagnosticIssue(
                $"{m.Device} runs at {m.CurrentHz} Hz but supports up to {m.MaxHzAtCurrentRes} Hz at this resolution.",
                "A lower-than-supported refresh rate wastes display capability and can make motion less smooth.",
                "Settings > System > Display > Advanced display > Select a refresh rate.",
                "Manual", "None"));

        // Game DVR / background recording
        if (s.GameDvrPolicyPresent != true && (s.GameDvrUserState is null || s.GameDvrUserState != 0))
            issues.Add(new DiagnosticIssue(
                "Game DVR background recording is enabled.",
                "Background clip capture continuously uses encoder, GPU and disk resources.",
                "Disable it on the Gaming page.",
                "Automatic (Gaming page)", "Low"));

        // HAGS
        if (s.HagsMode is not 2)
            issues.Add(new DiagnosticIssue(
                "Hardware-accelerated GPU scheduling is not explicitly enabled.",
                "HAGS effects vary by GPU, driver and game; some systems benefit, others do not.",
                "You can toggle it on the Gaming page. A restart is required. Results are workload-specific.",
                "Automatic (Gaming page)", "Low"));

        // Power plan
        if (!s.PowerPlan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase) &&
            !s.PowerPlan.Contains("High performance", StringComparison.OrdinalIgnoreCase) &&
            !s.PowerPlan.Contains("power plan unavailable"))
            issues.Add(new DiagnosticIssue(
                $"The active power plan is \"{s.PowerPlan}\".",
                "Power saver or other conservative plans can cap CPU behaviour during sustained loads.",
                "Activate the best supported performance plan on the Power page.",
                "Automatic (Power page)", "Low"));

        // NVIDIA specifics
        if (nvidia.Present)
        {
            if (nvidia.RebarState is string rebar && rebar.Contains("Disable", StringComparison.OrdinalIgnoreCase))
                issues.Add(new DiagnosticIssue(
                    "Resizable BAR reports disabled.",
                    "ReBAR can improve some games; enabling it is a BIOS/UEFI setting and depends on GPU + motherboard support.",
                    "Check Resizable BAR / Above 4G Decoding in BIOS/UEFI. This tool will not modify firmware.",
                    "Manual (BIOS)", "Low"));
            issues.Add(new DiagnosticIssue(
                $"NVIDIA driver {nvidia.Driver} — {nvidia.Vram} VRAM detected.",
                "Up-to-date drivers include game-specific fixes. Only update from NVIDIA's official site.",
                "Optionally check for driver updates via the NVIDIA App.",
                "Manual", "None"));
        }

        return issues;
    }
}
