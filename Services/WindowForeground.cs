using System.Runtime.InteropServices;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Brings an existing top-level window to the foreground while respecting the Windows
/// foreground-lock rules: restores when minimized, briefly attaches to the foreground
/// thread's input queue so SetForegroundWindow is accepted, then detaches. No state is
/// left changed — the window is not made topmost.
/// </summary>
internal static class WindowForeground
{
    private const int SwRestore = 9;

    public static void BringToFront(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        try
        {
            ShowWindow(hWnd, SwRestore);

            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint currentThread = (uint)Environment.CurrentManagedThreadId;
            bool attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
                attached = AttachThreadInput(currentThread, foregroundThread, true);

            BringWindowToTop(hWnd);
            SetForegroundWindow(hWnd);
            SetActiveWindow(hWnd);

            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);

            ShowWindow(hWnd, 5); // SW_SHOW
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
