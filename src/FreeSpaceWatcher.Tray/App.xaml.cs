using System.Windows;
using FreeSpaceWatcher.Tray.Instance;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FreeSpaceWatcher.Tray;

/// <summary>The tray application; it has no main window, runs once per user session, and runs until shut down explicitly.</summary>
/// <remarks>
/// Started with <c>--uninstall-notifications</c>, it removes its toast notification registration for the current user and
/// exits (0 on success, 1 on failure) without showing the tray or taking the single-instance mutex. Otherwise it takes
/// <c>--theme light|dark|system</c> and <c>--open alerts|settings|status</c> (see <see cref="TrayArguments"/>); a second launch
/// while a tray runs hands its <c>--open</c> window to that tray through <see cref="TrayInstanceChannel"/> and exits.
/// </remarks>
public sealed partial class App : Application, IDisposable
{
    private const string UninstallNotificationsSwitch = "--uninstall-notifications";
    private const string InstanceMutexName = @"Local\FreeSpaceWatcher.Tray";
    private static readonly TimeSpan ChannelStopWait = TimeSpan.FromSeconds(2);
    private readonly TrayLog _log = new(TrayLog.DefaultPath);
    private readonly TrayArguments _arguments;
    private Mutex? _instanceMutex;
    private TrayHost? _host;
    private TrayInstanceChannel? _channel;

    /// <summary>Initializes a new instance of the <see cref="App"/> class and applies the <c>--theme</c> before App.xaml loads.</summary>
    /// <remarks>
    /// The theme is set here, before the resources in App.xaml exist, so the Fluent dictionaries are in place when the shared
    /// styles resolve the Fluent control styles they are based on.
    /// </remarks>
    public App()
    {
        _arguments = TrayArguments.Parse(Environment.GetCommandLineArgs()[1..]);
        // ThemeMode is experimental in .NET 10 (diagnostic WPF0001, "may be modified or removed in future releases":
        // https://learn.microsoft.com/dotnet/api/system.windows.application.thememode?view=windowsdesktop-10.0); it is the
        // only way to get the built-in Fluent theme in light, dark or system mode.
#pragma warning disable WPF0001
        ThemeMode = _arguments.Theme switch
        {
            TrayTheme.Light => ThemeMode.Light,
            TrayTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }

    /// <summary>Stops the second-launch channel, removes the tray icon, stops the pipe client and releases the single-instance mutex.</summary>
    public void Dispose()
    {
        if (_channel is not null && !_channel.DisposeAsync().AsTask().Wait(ChannelStopWait))
        {
            _log.Warning("The second-launch channel did not stop within 2 s of exit.", null);
        }

        _channel = null;
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
        HookUnhandledExceptions();
        if (e.Args is [UninstallNotificationsSwitch])
        {
            Shutdown(UninstallNotifications());
            return;
        }

        foreach (string problem in _arguments.Problems)
        {
            _log.Warning(problem, null);
        }

        Mutex mutex = new(true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            if (_arguments.Open is TrayWindow window)
            {
                TrayInstanceChannel.TrySend(TrayInstanceChannel.SessionPipeName, window, _log);
            }

            Shutdown();
            return;
        }

        _instanceMutex = mutex;
        _host = new TrayHost(Dispatcher, _log);
        _host.Start(_arguments.Open);
        _channel = TrayInstanceChannel.Listen(TrayInstanceChannel.SessionPipeName, window => Dispatcher.BeginInvoke(() => _host?.Open(window)), _log);
    }

    // The tray is the service's only window, so a failed UI action is recorded and swallowed rather than taking the app down.
    private void HookUnhandledExceptions()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            _log.Warning("Unhandled UI exception; the tray keeps running.", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _log.Warning("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log.Warning("Fatal unhandled exception; the tray is exiting.", e.ExceptionObject as Exception);
    }

    // The uninstall script waits for this process to exit; a crash dialog would keep it, and the files it runs from, alive.
#pragma warning disable CA1031
    private int UninstallNotifications()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
            return 0;
        }
        catch (Exception ex)
        {
            _log.Warning("Removing the toast notification registration failed.", ex);
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
