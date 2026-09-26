using System.Windows;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Single-instance guard runs before any window or application state is created.
        if (!SingleInstance.TryAcquire())
        {
            // Existing instance: ask it to come to the foreground, then exit quietly.
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

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
