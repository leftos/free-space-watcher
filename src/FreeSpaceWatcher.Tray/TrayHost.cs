using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Alerts;
using FreeSpaceWatcher.Tray.Icons;
using FreeSpaceWatcher.Tray.Pipe;
using FreeSpaceWatcher.Tray.Settings;
using FreeSpaceWatcher.Tray.Status;
using FreeSpaceWatcher.Tray.Toasts;
using H.NotifyIcon;
using Microsoft.Toolkit.Uwp.Notifications;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray;

/// <summary>Wires the tray: the notify icon, the pipe client's events onto the UI thread, the windows and the toasts.</summary>
public sealed class TrayHost : ITrayShell, IDisposable
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan OpenWithoutServiceAfter = TimeSpan.FromSeconds(5);
    private readonly Dispatcher _dispatcher;
    private readonly ITrayLog _log;
    private readonly PipeClient _client;
    private readonly MessageBoxDialogs _dialogs = new();
    private readonly ShellActions _shellActions;
    private readonly Dictionary<TrayState, DrawingIcon> _icons;
    private readonly TrayIconSetter _iconSetter;
    private readonly TrayViewModel _tray;
    private readonly TaskbarIcon _taskbarIcon;
    private AlertsWindow? _alertsWindow;
    private SettingsWindow? _settingsWindow;
    private StatusWindow? _statusWindow;
    private TrayWindow? _pendingOpen;
    private DispatcherTimer? _pendingOpenTimer;

    /// <summary>Initializes the host; nothing is shown or connected until <see cref="Start"/>.</summary>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="log">The tray's log, handed to everything that logs.</param>
    public TrayHost(Dispatcher dispatcher, ITrayLog log)
    {
        _dispatcher = dispatcher;
        _log = log;
        _client = new PipeClient(PipeProtocol.PipeName, PipeClient.DefaultRequestTimeout, log);
        _shellActions = new ShellActions(ShellActions.DefaultElevateHelperPath, log);
        _icons = Enum.GetValues<TrayState>().ToDictionary(s => s, s => TrayIconRenderer.ToIcon(TrayIconRenderer.Render(s)));
        _tray = new TrayViewModel(_client, this, log);
        _taskbarIcon = CreateTaskbarIcon();
        _iconSetter = new TrayIconSetter(_taskbarIcon, _icons);
    }

    /// <summary>Shows the icon, listens for toast clicks and starts connecting.</summary>
    /// <param name="open">
    /// A window to open once the service connects, or after 5 s without a connection so a stopped service still shows; null for none.
    /// </param>
    public void Start(TrayWindow? open)
    {
        _pendingOpen = open;
        if (open is not null)
        {
            _pendingOpenTimer = new DispatcherTimer(OpenWithoutServiceAfter, DispatcherPriority.Normal, (_, _) => OpenPending(), _dispatcher);
        }

        _tray.PropertyChanged += OnTrayPropertyChanged;
        _client.ConnectionChanged += (_, connected) => Post(() => OnConnectionChangedAsync(connected));
        _client.StatusReceived += (_, status) => Post(() => OnStatus(status));
        _client.AlertReceived += (_, alert) => Post(() => OnAlertAsync(alert));
        _client.AlertsChanged += (_, _) => Post(OnAlertsChangedAsync);
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        UpdateIcon();
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        _client.Start();
    }

    /// <inheritdoc/>
    public async Task<AlertsViewModel> ShowAlertsAsync(string? alertId)
    {
        if (_alertsWindow?.DataContext is not AlertsViewModel viewModel)
        {
            viewModel = new AlertsViewModel(_client, _dialogs, _shellActions, _log);
            _alertsWindow = new AlertsWindow { DataContext = viewModel };
            _alertsWindow.Closed += (_, _) => _alertsWindow = null;
            _alertsWindow.Show();
        }

        _alertsWindow.Activate();
        await viewModel.SelectAlertAsync(alertId);
        return viewModel;
    }

    /// <inheritdoc/>
    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            SettingsViewModel viewModel = new(_client, SettingsViewModel.ReadyDriveLetters, CultureInfo.CurrentCulture);
            viewModel.SaveSucceeded += (_, _) =>
            {
                _settingsWindow?.Close();
                Post(_tray.LoadConfigAsync);
            };
            _settingsWindow = new SettingsWindow { DataContext = viewModel };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            viewModel.IsConnected = _client.IsConnected;
        }

        _settingsWindow.Activate();
    }

    /// <inheritdoc/>
    public void ShowStatus()
    {
        if (_statusWindow is null)
        {
            _statusWindow = new StatusWindow { DataContext = new StatusViewModel(_client, this, _log) };
            _statusWindow.Closed += (_, _) => _statusWindow = null;
            SyncStatusWindow();
            _statusWindow.Show();
        }

        _statusWindow.Activate();
    }

    /// <summary>Opens (or activates) a window.</summary>
    /// <param name="window">The window.</param>
    public void Open(TrayWindow window)
    {
        switch (window)
        {
            case TrayWindow.Alerts:
                Post(() => ShowAlertsAsync(null));
                break;
            case TrayWindow.Settings:
                ShowSettings();
                break;
            default:
                ShowStatus();
                break;
        }
    }

    /// <inheritdoc/>
    public void ShowAlertToast(Alert alert) => AlertToasts.ShowAlert(alert);

    /// <inheritdoc/>
    public void ShowSummaryToast(int count) => AlertToasts.ShowSummary(count);

    /// <inheritdoc/>
    public void ShowProcessActionToast(ProcessActionToast outcome) => AlertToasts.ShowProcessAction(outcome);

    /// <inheritdoc/>
    public void RemoveAlertToasts() => AlertToasts.RemoveAll();

    /// <inheritdoc/>
    public void RemoveAlertToast(string drive) => AlertToasts.RemoveForDrive(drive);

    /// <inheritdoc/>
    public void OpenFolder(string folder)
    {
        if (_shellActions.OpenFolder(folder) is string error)
        {
            ShowError("Open folder", error);
        }
    }

    /// <inheritdoc/>
    public void ShowError(string title, string message) => _dialogs.ShowError(title, message);

    /// <inheritdoc/>
    public void Shutdown() => Application.Current.Shutdown();

    /// <summary>Removes the icon, closes the windows and stops the pipe client.</summary>
    public void Dispose()
    {
        ToastNotificationManagerCompat.OnActivated -= OnToastActivated;
        _pendingOpenTimer?.Stop();
        _alertsWindow?.Close();
        _settingsWindow?.Close();
        _statusWindow?.Close();
        _taskbarIcon.Dispose();
        if (!_client.DisposeAsync().AsTask().Wait(ShutdownWait))
        {
            _log.Warning("The pipe client did not stop within 2 s of exit.", null);
        }

        foreach (DrawingIcon icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private TaskbarIcon CreateTaskbarIcon()
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "Status…", Command = _tray.ShowStatusCommand });
        menu.Items.Add(new MenuItem { Header = "Acknowledge all", Command = _tray.AcknowledgeAllCommand });
        menu.Items.Add(new MenuItem { Header = "Alerts…", Command = _tray.ShowAlertsCommand });
        menu.Items.Add(new MenuItem { Header = "Settings…", Command = _tray.ShowSettingsCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit", Command = _tray.ExitCommand });
        return new TaskbarIcon
        {
            ContextMenu = menu,
            LeftClickCommand = _tray.ShowStatusCommand,
            NoLeftClickDelay = true,
            ToolTipText = _tray.ToolTipText,
        };
    }

    private void Post(Func<Task> work) =>
        _dispatcher.BeginInvoke(
            new Action(async () =>
            {
                await work();
            })
        );

    private void Post(Action work) => _dispatcher.BeginInvoke(work);

    private async Task OnConnectionChangedAsync(bool connected)
    {
        if (_settingsWindow?.DataContext is SettingsViewModel settings)
        {
            settings.IsConnected = connected;
        }

        await _tray.OnConnectionChangedAsync(connected);
        if (connected)
        {
            OpenPending();
        }
    }

    private void OpenPending()
    {
        _pendingOpenTimer?.Stop();
        _pendingOpenTimer = null;
        if (_pendingOpen is TrayWindow window)
        {
            _pendingOpen = null;
            Open(window);
        }
    }

    private void SyncStatusWindow()
    {
        if (_statusWindow?.DataContext is StatusViewModel status)
        {
            status.Update(_tray.IsConnected, _tray.Status, _tray.Config, _tray.Alerts, DateTimeOffset.Now);
        }
    }

    private void OnStatus(StatusResponse status)
    {
        _tray.OnStatus(status);
        if (_settingsWindow?.DataContext is SettingsViewModel settings)
        {
            settings.ApplyStatus(status);
        }
    }

    private async Task OnAlertAsync(Alert alert)
    {
        await _tray.OnAlertAsync(alert);
        if (_alertsWindow?.DataContext is AlertsViewModel alerts)
        {
            await alerts.SelectAlertAsync(null);
        }
    }

    private async Task OnAlertsChangedAsync()
    {
        await _tray.OnAlertsChangedAsync();
        if (_alertsWindow?.DataContext is AlertsViewModel alerts)
        {
            await alerts.SelectAlertAsync(null);
        }
    }

    private void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var request = ToastRequest.Parse(e.Argument);
        Post(() => _tray.HandleToastAsync(request));
    }

    private void OnTrayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateIcon();
        SyncStatusWindow();
    }

    private void UpdateIcon()
    {
        _iconSetter.Apply(_tray.State);
        _taskbarIcon.ToolTipText = _tray.ToolTipText;
    }
}
