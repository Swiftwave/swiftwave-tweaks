using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Minimal notification-area icon implemented directly over Shell_NotifyIcon so the
/// self-contained package gains no extra framework. Used for the "minimize to tray on
/// close" setting: left click restores the window, right click offers Open / Exit.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonDblClk = 0x0203;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const int NifMessage = 1;
    private const int NifIcon = 2;
    private const int NifTip = 4;

    private readonly Window owner;
    private readonly Action restore;
    private readonly Action exit;
    private readonly IntPtr handle;
    private readonly HwndSource hook;
    private NOTIFYICONDATA data;
    private bool added;
    private ContextMenu? menu;

    public TrayIcon(Window owner, string tooltip, Action restore, Action exit)
    {
        this.owner = owner;
        this.restore = restore;
        this.exit = exit;

        handle = new WindowInteropHelper(owner).Handle;
        data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = handle,
            uID = 0x53,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmApp,
            hIcon = LoadAppIcon(),
            szTip = tooltip
        };

        hook = (HwndSource?)HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("no hwnd source");
        hook.AddHook(WndProc);
    }

    public void Show()
    {
        if (added) return;
        Shell_NotifyIcon(NimAdd, ref data);
        added = true;
    }

    public void Hide()
    {
        if (!added) return;
        Shell_NotifyIcon(NimDelete, ref data);
        added = false;
    }

    public void Dispose()
    {
        Hide();
        hook.RemoveHook(WndProc);
        if (data.hIcon != IntPtr.Zero) DestroyIcon(data.hIcon);
        data.hIcon = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmApp || !added) return IntPtr.Zero;
        switch (lParam.ToInt32())
        {
            case WmLButtonUp:
            case WmLButtonDblClk:
                restore();
                handled = true;
                break;
            case WmRButtonUp:
                OpenMenu();
                handled = true;
                break;
        }
        return IntPtr.Zero;
    }

    private void OpenMenu()
    {
        if (menu is null)
        {
            menu = new ContextMenu
            {
                PlacementTarget = owner,
                Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
                Background = Ui.Card,
                BorderBrush = Ui.Line,
                Foreground = Ui.TextWhite
            };
            var open = new MenuItem { Header = "Open" };
            open.Click += (_, _) => { menu.IsOpen = false; restore(); };
            var exitItem = new MenuItem { Header = "Exit" };
            exitItem.Click += (_, _) => { menu.IsOpen = false; exit(); };
            menu.Items.Add(open);
            menu.Items.Add(exitItem);
        }
        menu.IsOpen = true;
    }

    /// <summary>
    /// Loads the application icon for the tray from the pack resource. The .ico container is
    /// parsed manually and the 32px entry is handed to CreateIconFromResourceEx.
    /// </summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GameReady.ico"));
            if (resource is null) return IntPtr.Zero;
            using var stream = new BinaryReader(resource.Stream);
            byte[] all = stream.ReadBytes((int)resource.Stream.Length);
            if (all.Length < 6 || BitConverter.ToUInt16(all, 0) != 0 || all[2] != 1) return IntPtr.Zero;
            int count = BitConverter.ToUInt16(all, 4);
            int best = -1, bestDelta = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int at = 6 + i * 16;
                int width = all[at] == 0 ? 256 : all[at];
                int delta = Math.Abs(width - 32);
                if (delta < bestDelta) { bestDelta = delta; best = at; }
            }
            if (best < 0) return IntPtr.Zero;
            int size = BitConverter.ToInt32(all, best + 8);
            int offset = BitConverter.ToInt32(all, best + 12);
            byte[] image = new byte[size];
            Array.Copy(all, offset, image, 0, size);
            if (CreateIconFromResourceEx(image, (uint)size, true, 0x00030000, 0, 0, 0) is IntPtr icon && icon != IntPtr.Zero)
                return icon;
        }
        catch (Exception exception)
        {
            SafeLog.Write("Tray icon load failed", exception);
        }
        return IntPtr.Zero;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] presbits, uint dwResSize, bool fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }
}
