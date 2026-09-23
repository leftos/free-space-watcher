using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FreeSpaceWatcher.Tray;

/// <summary>The tray application; it has no main window, runs once per user session, and runs until shut down explicitly.</summary>
/// <remarks>
/// Started with <c>--uninstall-notifications</c>, it removes its toast notification registration for the current user and
/// exits (0 on success, 1 on failure) without showing the tray or taking the single-instance mutex.
/// </remarks>
public sealed partial class App : Application, IDisposable
{
    private const string UninstallNotificationsSwitch = "--uninstall-notifications";
    private const string InstanceMutexName = @"Local\FreeSpaceWatcher.Tray";
    private Mutex? _instanceMutex;
    private TrayHost? _host;

    /// <summary>Removes the tray icon, stops the pipe client and releases the single-instance mutex.</summary>
    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    /// <inheritdoc/>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is [UninstallNotificationsSwitch])
        {
            Shutdown(UninstallNotifications());
            return;
        }

        Mutex mutex = new(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            Shutdown();
            return;
        }

        _instanceMutex = mutex;
        _host = new TrayHost(Dispatcher);
        _host.Start();
    }

    // The uninstall script waits for this process to exit; a crash dialog would keep it, and the files it runs from, alive.
#pragma warning disable CA1031
    private static int UninstallNotifications()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            return 0;
        }
        catch (Exception ex)
        {
            TrayLog.Warning("Removing the toast notification registration failed.", ex);
            return 1;
        }
    }
#pragma warning restore CA1031

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
}
