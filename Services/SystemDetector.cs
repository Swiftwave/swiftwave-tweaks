using System.Collections.Generic;
using System.Linq;
using System.Management;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Core;

public sealed record RamModule(string Slot, double CapacityGb, int? SpeedMts, int? ConfiguredSpeedMts);
public sealed record DriveInfo(string Letter, string VolumeLabel, string Model, string BusKind, double SizeGb, double FreeGb);
public sealed record MonitorInfo(string Device, int Width, int Height, int CurrentHz, int MaxHzAtCurrentRes);

public sealed record SystemSnapshot(
    string WindowsEdition,
    string WindowsVersion,
    string WindowsBuild,
    string CpuName,
    int CpuCores,
    int CpuThreads,
    string GpuName,
    string GpuDriver,
    bool HasNvidia,
    double MemoryGb,
    IReadOnlyList<RamModule> RamModules,
    IReadOnlyList<DriveInfo> Drives,
    string PowerPlan,
    string PowerPlanGuid,
    IReadOnlyList<MonitorInfo> Monitors,
    int? HagsMode,
    int? GameDvrUserState,
    bool GameDvrPolicyPresent,
    int? GameBarState,
    int? GameModeState,
    int ProcessCount)
{
    public string WindowsLine => $"{WindowsEdition} {WindowsVersion} (build {WindowsBuild})";

    public string ToDisplayText()
    {
        var ram = RamModules.Count == 0
            ? $"{MemoryGb:N0} GB total"
            : $"{MemoryGb:N0} GB total across {RamModules.Count} module(s)\n" +
              string.Join("\n", RamModules.Select(m =>
                  $"  {m.Slot}: {m.CapacityGb:N0} GB @ {m.ConfiguredSpeedMts?.ToString() ?? "?"} MT/s" +
                  (m.SpeedMts is int rated && m.ConfiguredSpeedMts is int cfg && cfg < rated ? $" (rated {rated} MT/s)" : "")));
        var drives = Drives.Count == 0 ? "No fixed drives detected." : string.Join("\n", Drives.Select(d =>
            $"  {d.Letter} {d.VolumeLabel,-12} {d.BusKind,-5} {d.FreeGb,6:N0} GB free of {d.SizeGb,6:N0} GB — {d.Model}"));
        var monitors = Monitors.Count == 0 ? "Display information unavailable." : string.Join("\n", Monitors.Select(m =>
            $"  {m.Device}: {m.Width}x{m.Height} @ {m.CurrentHz} Hz" + (m.MaxHzAtCurrentRes > m.CurrentHz ? $" (supports up to {m.MaxHzAtCurrentRes} Hz)" : "")));
        return
            $"Windows\n  {WindowsLine}\n\n" +
            $"Processor\n  {CpuName} — {CpuCores} cores / {CpuThreads} threads\n\n" +
            $"Graphics\n  {GpuName}\n  Driver {GpuDriver}\n\n" +
            $"Memory\n  {ram}\n\n" +
            $"Displays\n  {monitors}\n\n" +
            $"Active power plan\n  {PowerPlan}\n\n" +
            $"Storage\n{drives}\n\n" +
            $"Processes running\n  {ProcessCount}";
    }
}

public static class SystemDetector
{
    private static string Wmi(string query, string property, string fallback = "")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            return searcher.Get().Cast<ManagementBaseObject>()
                .Select(x => x[property]?.ToString())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? fallback;
        }
        catch { return fallback; }
    }

    private static List<ManagementBaseObject> WmiAll(string path, string @namespace = "root\\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@namespace, path);
            return searcher.Get().Cast<ManagementBaseObject>().ToList();
        }
        catch { return []; }
    }

    public static Task<SystemSnapshot> CollectAsync() => Task.Run(Collect);

    public static SystemSnapshot Collect()
    {
        var cv = RegistryRead.CurrentVersionValue;
        string edition = cv("ProductName", "Windows");
        string displayVersion = cv("DisplayVersion", cv("ReleaseId", ""));
        string build = cv("CurrentBuildNumber", "0");
        string ubr = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR")?.ToString() ?? "0";
        if (build != "0" && int.TryParse(build, out var b) && b >= 22000 && edition.Contains("Windows 10"))
            edition = edition.Replace("Windows 10", "Windows 11");

        var cpuObj = WmiAll("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").FirstOrDefault();
        string cpu = cpuObj?["Name"]?.ToString()?.Trim() ?? "Processor information unavailable";

        var gpus = WmiAll("SELECT Name, DriverVersion, DriverDate, Status FROM Win32_VideoController");
        var real = gpus.FirstOrDefault(g =>
        {
            var n = (g["Name"]?.ToString() ?? "");
            return !n.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("Basic", StringComparison.OrdinalIgnoreCase)
                && !n.Contains("Indirect", StringComparison.OrdinalIgnoreCase);
        }) ?? gpus.FirstOrDefault();
        string gpuName = real?["Name"]?.ToString() ?? "Graphics information unavailable";
        string gpuDriver = real?["DriverVersion"]?.ToString() ?? "";
        var gpuDate = real?["DriverDate"] is DateTime d ? d.ToString("yyyy-MM-dd") : null;
        if (!string.IsNullOrEmpty(gpuDriver) && gpuDriver.Length > 5)
            gpuDriver = $"{gpuDriver} ({gpuDate ?? "driver date unavailable"})";
        bool hasNvidia = gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

        var modules = WmiAll("SELECT BankLabel, Capacity, Speed, ConfiguredClockSpeed FROM Win32_PhysicalMemory")
            .Select(m => new RamModule(
                m["BankLabel"]?.ToString() ?? "?",
                Convert.ToDouble(m["Capacity"] ?? 0) / 1_073_741_824,
                m["Speed"] is uint s ? (int?)s : null,
                m["ConfiguredClockSpeed"] is uint c ? (int?)c : null))
            .ToList();
        double totalGb = WmiAll("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem")
            .Select(m => Convert.ToDouble(m["TotalPhysicalMemory"] ?? 0) / 1_073_741_824).FirstOrDefault();

        var drives = CollectDrives();
        var (planName, planGuid) = PowerPlanReader.ReadActive();
        var monitors = DisplayService.EnumerateMonitors();

        int? hags = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        int? gameDvr = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default,
            @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        bool dvrPolicy = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64,
            @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR") is not null;
        int? gameBar = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default,
            @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled");
        int? gameMode = RegHelper.ReadDword(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default,
            @"Software\Microsoft\GameBar", "AutoGameModeEnabled")
            ?? RegHelper.ReadDword(Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Default,
            @"Software\Microsoft\GameBar", "AllowAutoGameModeEnabled");

        return new SystemSnapshot(
            edition, displayVersion, $"{build}.{ubr}",
            cpu,
            int.TryParse(Wmi("SELECT NumberOfCores FROM Win32_Processor", "NumberOfCores", "0"), out var cores) ? cores : 0,
            int.TryParse(Wmi("SELECT NumberOfLogicalProcessors FROM Win32_Processor", "NumberOfLogicalProcessors", "0"), out var threads) ? threads : 0,
            gpuName, gpuDriver, hasNvidia,
            totalGb, modules, drives,
            planName, planGuid, monitors,
            hags, gameDvr, dvrPolicy, gameBar, gameMode,
            System.Diagnostics.Process.GetProcesses().Length);
    }

    private static List<DriveInfo> CollectDrives()
    {
        var list = new List<DriveInfo>();
        try
        {
            var logical = WmiAll("SELECT DeviceID, VolumeName, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            var psMap = DriveMappingViaPowerShell();
            foreach (var d in logical)
            {
                string letter = d["DeviceID"]?.ToString() ?? "";
                double size = Convert.ToDouble(d["Size"] ?? 0) / 1_073_741_824;
                double free = Convert.ToDouble(d["FreeSpace"] ?? 0) / 1_073_741_824;
                string model = ResolveDiskModel(letter);
                string bus;
                if (model.Contains("unavailable") && psMap.TryGetValue(letter, out var mapped))
                    (model, bus) = (mapped.Model, mapped.Bus);
                else
                    bus = ClassifyBus(model);
                list.Add(new DriveInfo(letter, d["VolumeName"]?.ToString() ?? "", model, bus, size, free));
            }
        }
        catch { }
        return list;
    }

    /// <summary>Fallback mapping through Get-Partition/Get-PhysicalDisk when WMI associations are unavailable.</summary>
    private static Dictionary<string, (string Model, string Bus)> DriveMappingViaPowerShell()
    {
        var map = new Dictionary<string, (string, string)>();
        try
        {
            var r = ProcessRunner.RunPowerShell(
                "Get-Partition | Where-Object DriveLetter | ForEach-Object { $d = Get-PhysicalDisk -DeviceNumber $_.DiskNumber -ErrorAction SilentlyContinue; " +
                "if ($d) { '{0}|{1}|{2}|{3}' -f $_.DriveLetter, $d.FriendlyName, $d.BusType, $d.MediaType } }");
            if (r.ExitCode == 0)
            {
                foreach (var line in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Trim().Split('|');
                    if (parts.Length == 4)
                    {
                        string bus = parts[2].Equals("NVMe", StringComparison.OrdinalIgnoreCase) ? "NVMe"
                            : parts[3].Equals("SSD", StringComparison.OrdinalIgnoreCase) ? "SSD"
                            : parts[2].Equals("USB", StringComparison.OrdinalIgnoreCase) ? "USB"
                            : parts[3].Equals("HDD", StringComparison.OrdinalIgnoreCase) ? "HDD" : "HDD/other";
                        map[parts[0] + ":"] = (parts[1], bus);
                    }
                }
            }
        }
        catch { }
        return map;
    }

    private static string ResolveDiskModel(string letter)
    {
        try
        {
            using var disks = new ManagementObjectSearcher("SELECT DeviceID, Model, PNPDeviceID FROM Win32_DiskDrive").Get();
            foreach (ManagementBaseObject disk in disks)
            {
                string deviceId = disk["DeviceID"]?.ToString() ?? "";
                using var parts = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{deviceId.Replace("\\", "\\\\")}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition").Get();
                foreach (ManagementBaseObject part in parts)
                {
                    using var vols = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{part["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition").Get();
                    foreach (ManagementBaseObject vol in vols)
                        if (string.Equals(vol["DeviceID"]?.ToString(), letter, StringComparison.OrdinalIgnoreCase))
                        {
                            string model = disk["Model"]?.ToString() ?? "";
                            string pnp = disk["PNPDeviceID"]?.ToString() ?? "";
                            if (pnp.Contains("NVME", StringComparison.OrdinalIgnoreCase)) return model + " (NVMe)";
                            return model;
                        }
                }
            }
        }
        catch { }
        return "Model unavailable";
    }

    private static string ClassifyBus(string model)
    {
        if (model.Contains("(NVMe)")) return "NVMe";
        if (model.Contains("SSD", StringComparison.OrdinalIgnoreCase)) return "SSD";
        return "HDD/other";
    }
}

/// <summary>Reads the active power scheme via powercfg (independent verification helper).</summary>
public static class PowerPlanReader
{
    public static (string Name, string Guid) ReadActive()
    {
        var outp = ProcessRunner.RunCapture("powercfg", "/getactivescheme");
        if (outp is { } r)
        {
            var m = System.Text.RegularExpressions.Regex.Match(r.Output, @"GUID:\s*([0-9a-fA-F-]{36})\s*\((.*)\)");
            if (m.Success) return (m.Groups[2].Value.Trim(), m.Groups[1].Value);
        }
        return ("Power plan unavailable", "");
    }

    public static List<(string Guid, string Name)> ListPlans()
    {
        var plans = new List<(string, string)>();
        var outp = ProcessRunner.RunCapture("powercfg", "/list");
        if (outp is { } r)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(r.Output, @"GUID:\s*([0-9a-fA-F-]{36})\s*\((.*?)\)"))
                plans.Add((m.Groups[1].Value, m.Groups[2].Value.Trim()));
        }
        return plans;
    }
}
