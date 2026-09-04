using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace SwiftwaveTweaks.Services;

public record NvidiaInfo(bool Present, string Name, string Driver, string Vram, string? Temperature, string? PowerDraw, string? RebarState)
{
    public static readonly NvidiaInfo Absent = new(false, "No NVIDIA GPU detected", "", "", null, null, null);
}

public static class NvidiaService
{
    /// <summary>Reads GPU facts through the supported nvidia-smi CLI. Read-only. Returns Absent when unavailable.</summary>
    public static NvidiaInfo Query()
    {
        var gpu = ProcessRunner.RunCapture("nvidia-smi", "--query-gpu=name,driver_version,memory.total,temperature.gpu,power.draw --format=csv,noheader");
        if (gpu is null || gpu.Value.ExitCode != 0)
            return NvidiaInfo.Absent;
        var line = gpu.Value.Output.Split('\n').FirstOrDefault(l => l.Contains(',')) ?? "";
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();
        if (parts.Length < 3) return NvidiaInfo.Absent;

        string? rebar = null;
        var full = ProcessRunner.RunCapture("nvidia-smi", "-q");
        if (full is { } f && f.ExitCode == 0)
        {
            var m = System.Text.RegularExpressions.Regex.Match(f.Output, @"ReBAR\s*:\s*(\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            rebar = m.Success ? m.Groups[1].Value : null;
        }
        return new NvidiaInfo(true,
            parts[0], parts[1], parts[2],
            parts.Length > 3 && parts[3] != "N/A" ? parts[3] + " °C" : null,
            parts.Length > 4 && parts[4] != "N/A" ? parts[4] : null,
            rebar);
    }
}

public static class DisplayService
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int ENUM_REGISTRY_SETTINGS = -2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    /// <summary>Enumerates attached monitors: current resolution/refresh rate and the maximum refresh rate available at the current resolution.</summary>
    public static List<Core.MonitorInfo> EnumerateMonitors()
    {
        var result = new List<Core.MonitorInfo>();
        foreach (var device in EnumDeviceNames())
        {
            var dm = new DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm)) continue;
            int w = (int)dm.dmPelsWidth, h = (int)dm.dmPelsHeight, hz = (int)dm.dmDisplayFrequency;
            int maxHz = hz;
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
                if ((int)dm.dmPelsWidth == w && (int)dm.dmPelsHeight == h && dm.dmBitsPerPel == 32)
                    maxHz = Math.Max(maxHz, (int)dm.dmDisplayFrequency);
            result.Add(new Core.MonitorInfo(device, w, h, hz, maxHz));
        }
        return result;
    }

    private static IEnumerable<string> EnumDeviceNames()
    {
        var list = new List<string>();
        for (uint i = 0; ; i++)
        {
            var d = new DISPLAY_DEVICE();
            d.cb = (ushort)Marshal.SizeOf<DISPLAY_DEVICE>();
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if ((d.StateFlags & 1) != 0) list.Add(d.DeviceName); // DISPLAY_DEVICE_ATTACHED_TO_DESKTOP
        }
        return list;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
}

public static class RestorePointService
{
    /// <summary>Attempts to create a system restore point. Returns a truthful outcome; never pretends success.</summary>
    public static (bool Success, string Message) TryCreate(string description)
    {
        var script = $"Checkpoint-Computer -Description '{description.Replace("'", "''")}' -RestorePointType MODIFY_SETTINGS; $LastError=$?; \"$LastError\"";
        var r = ProcessRunner.RunElevatedPowerShellWithOutput(script);
        if (r.ExitCode == 0 && r.Output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase))
            return (true, "System restore point created.");
        return (false, "Restore point could not be created" +
            (r.Output.Trim().Length > 0 ? ": " + r.Output.Trim().Split('\n')[0] : ". System Protection may be disabled for this drive."));
    }
}
