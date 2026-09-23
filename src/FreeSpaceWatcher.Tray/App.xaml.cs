using System.Windows;

namespace FreeSpaceWatcher.Tray;

/// <summary>The tray application; it has no main window, runs once per user session, and runs until shut down explicitly.</summary>
public sealed partial class App : Application, IDisposable
{
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

    /// <inheritdoc/>
    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }
}
