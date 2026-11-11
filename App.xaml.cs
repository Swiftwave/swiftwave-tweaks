using System.Windows;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        bool headless = e.Args.Any(a =>
            string.Equals(a, "--v3-selftest", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--ui-selftest", StringComparison.OrdinalIgnoreCase));

        // Elevation gate. The manifest requests requireAdministrator, so Windows shows UAC before
        // this code runs (and never starts the process if the user declines). Reaching this point
        // without an elevated token means the manifest was stripped — in that case the app must
        // not pretend to be fully operational, because every HKLM/service/power write it offers
        // would fail. No restart loop: report once and exit.
        if (!Elevation.IsElevated())
        {
            SafeLog.Write("Startup aborted: process token is not elevated.");
            if (!headless)
            {
                MessageBox.Show(
                    "Swiftwave Tweaks requires administrator privileges.\n\n" +
                    "Start it again and approve the Windows User Account Control prompt.",
                    "Swiftwave Tweaks", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown(1);
            return;
        }

        // Apply the saved colour theme before any window or resource template is created, so
        // the first rendered frame already uses it. Headless self-tests resolve the same theme.
        var prefs = PreferencesStore.Read();
        ThemeService.Apply(prefs.Theme);
        if (!headless) ThemeService.StartWatching();

        // Headless self-test path: no window, no single-instance lock, exit with 0/1.
        if (e.Args.Any(a => string.Equals(a, "--v3-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SwiftwaveTweaks.Tests.V3SelfTest.Run();
            Shutdown(code);
            return;
        }

        // Headless UI composition test: builds the real page trees without showing the window.
        if (e.Args.Any(a => string.Equals(a, "--ui-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            int code = SwiftwaveTweaks.Tests.UiSelfTest.Run();
            Shutdown(code);
            return;
        }

        // Single-instance guard runs before any window or application state is created.
        if (!SingleInstance.TryAcquire())
        {
            // Existing instance: ask it to come to the foreground, then exit quietly.
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        // The HKCU Run entry is the authoritative autostart state; the stored preference lets a
        // moved/updated executable repair a stale entry instead of leaving a dead path behind.
        bool runOnStartup = SelfStartup.Reconcile(prefs.RunOnStartup);
        if (runOnStartup != prefs.RunOnStartup)
            PreferencesStore.Write(prefs with { RunOnStartup = runOnStartup });

        // "Start minimized" is a device-startup behaviour only: it turns the startup launch into a
        // tray-icon-only run. The HKCU Run command carries the startup argument and manual
        // launches never do, so a manually opened window always comes up in the foreground.
        SwiftwaveTweaks.MainWindow.LaunchFromStartup = e.Args.Any(a =>
            string.Equals(a, SelfStartup.StartupArgument, StringComparison.OrdinalIgnoreCase));

        // First instance: normal startup (StartupUri creates MainWindow), then listen.
        base.OnStartup(e);
        SingleInstance.StartListener(() =>
            Current.Dispatcher.Invoke(() =>
                (Current.MainWindow as MainWindow)?.ActivateFromSecondInstance()));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SingleInstance.Shutdown();
        base.OnExit(e);
    }
}
