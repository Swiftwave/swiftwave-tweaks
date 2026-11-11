using System.Runtime.InteropServices;

namespace SwiftwaveTweaks.Services;

/// <summary>One live reading. Availability flags are per metric: a failed metric is never reported as zero.</summary>
public sealed record TelemetrySample(
    bool CpuAvailable, double CpuPercent,
    bool MemoryAvailable, double MemoryPercent, double UsedGb, double TotalGb,
    TimeSpan Uptime);

/// <summary>
/// Live CPU / memory / uptime sampling through documented Windows APIs:
///  • CPU: GetSystemTimes deltas (idle vs. kernel+user) between samples.
///  • Memory: GlobalMemoryStatusEx physical totals.
///  • Uptime: Environment.TickCount64.
/// Collection runs on a thread-pool timer, never on the UI thread, and every read failure is
/// surfaced through the sample's availability flags instead of being replaced by zero.
/// </summary>
public sealed class LiveTelemetry : IDisposable
{
    private readonly Timer _timer;
    private readonly object _gate = new();
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _primed;
    private bool _disposed;

    /// <summary>Raised on the timer thread after every sample. Handlers must marshal to the UI thread.</summary>
    public event Action<TelemetrySample>? Sampled;

    public LiveTelemetry(TimeSpan? interval = null)
    {
        Read(); // prime the CPU deltas so the first tick after one second is already meaningful
        var period = interval ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Sample(), null, period, period);
    }

    private void Sample()
    {
        if (_disposed) return;
        TelemetrySample sample;
        try { sample = Read(); }
        catch (Exception ex)
        {
            SafeLog.Write("Live telemetry read failed.", ex);
            sample = new TelemetrySample(false, 0, false, 0, 0, 0, TimeSpan.Zero);
        }
        try { Sampled?.Invoke(sample); }
        catch (Exception ex) { SafeLog.Write("Live telemetry handler failed.", ex); }
    }

    private TelemetrySample Read()
    {
        bool cpuOk = false;
        double cpu = 0;
        lock (_gate)
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                ulong i = Combine(idle), k = Combine(kernel), u = Combine(user);
                if (_primed)
                {
                    // Kernel time includes idle time, so busy time is (kernel + user) - idle.
                    ulong di = i - _prevIdle, dk = k - _prevKernel, du = u - _prevUser;
                    ulong total = dk + du;
                    if (total > 0)
                    {
                        cpu = Math.Clamp(100.0 * (1.0 - (double)di / total), 0.0, 100.0);
                        cpuOk = true;
                    }
                }
                _prevIdle = i; _prevKernel = k; _prevUser = u;
                _primed = true;
            }
            else
            {
                _primed = false;
            }
        }

        bool memOk = false;
        double mem = 0, usedGb = 0, totalGb = 0;
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
        {
            totalGb = status.ullTotalPhys / 1_073_741_824.0;
            usedGb = (status.ullTotalPhys - status.ullAvailPhys) / 1_073_741_824.0;
            mem = Math.Clamp(usedGb / totalGb * 100.0, 0.0, 100.0);
            memOk = true;
        }

        return new TelemetrySample(cpuOk, cpu, memOk, mem, usedGb, totalGb,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static ulong Combine(FILETIME t) => ((ulong)t.dwHighDateTime << 32) | t.dwLowDateTime;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
