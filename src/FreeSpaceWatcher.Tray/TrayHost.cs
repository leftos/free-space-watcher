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
using Microsoft.Win32;

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
    private readonly TrayIconSetter _iconSetter;
    private readonly TrayViewModel _tray;
    private readonly TaskbarIcon _taskbarIcon;
    private TaskbarTheme _taskbarTheme;
    private AlertsWindow? _alertsWindow;
    private SettingsWindow? _settingsWindow;
    private StatusWindow? _statusWindow;
    private TrayWindow? _pendingOpen;
    private bool _pendingSelectLatest;
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
        _tray = new TrayViewModel(_client, this, log);
        _taskbarIcon = CreateTaskbarIcon();
        int iconSize = TrayIconRenderer.SmallIconPixels();
        _iconSetter = new TrayIconSetter(_taskbarIcon, key => TrayIconRenderer.RenderIcon(key, iconSize));
        _taskbarTheme = TaskbarThemeReader.Read();
    }

    /// <summary>Shows the icon, listens for toast clicks and starts connecting.</summary>
    /// <param name="open">
    /// A window to open once the service connects, or after 5 s without a connection so a stopped service still shows; null for none.
    /// </param>
    /// <param name="selectLatest">Whether the alerts window opened for <paramref name="open"/> selects the newest alert.</param>
    public void Start(TrayWindow? open, bool selectLatest)
    {
        _pendingOpen = open;
        _pendingSelectLatest = selectLatest;
        if (open is not null)
        {
            _pendingOpenTimer = new DispatcherTimer(OpenWithoutServiceAfter, DispatcherPriority.Normal, (_, _) => OpenPending(), _dispatcher);
        }

        _tray.PropertyChanged += OnTrayPropertyChanged;
        _client.ConnectionChanged += (_, connected) => Post(() => OnConnectionChangedAsync(connected));
        _client.StatusReceived += (_, status) => Post(() => OnStatus(status));
        _client.AlertReceived += (_, alert) => Post(() => OnAlertAsync(alert));
        _client.AlertsChanged += (_, _) => Post(OnAlertsChangedAsync);
        _client.AlertResolved += (_, alert) => Post(() => _tray.OnAlertResolved(alert));
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        UpdateIcon();
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        _client.Start();
    }

    /// <inheritdoc/>
    public async Task<AlertsViewModel> ShowAlertsAsync(string? alertId)
    {
        if (_alertsWindow?.DataContext is not AlertsViewModel viewModel)
        {
            viewModel = new AlertsViewModel(_client, _dialogs, _shellActions, _log, TimeProvider.System);
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
    public void ShowAlertToast(Alert alert) => AlertToasts.ShowAlert(alert, AlertToasts.FloorBytes(_tray.Config, alert));

    /// <inheritdoc/>
    public void ShowResolvedToast(Alert alert) => AlertToasts.ShowResolved(alert, AlertToasts.FloorBytes(_tray.Config, alert));

    /// <inheritdoc/>
    public void ShowSummaryToast(int count) => AlertToasts.ShowSummary(count);

    /// <inheritdoc/>
    public void ShowProcessActionToast(ProcessActionToast outcome) => AlertToasts.ShowProcessAction(outcome);

    /// <inheritdoc/>
    public void RemoveAlertToasts() => AlertToasts.RemoveAll();

    /// <inheritdoc/>
    public void RemoveAlertToast(string drive) => AlertToasts.RemoveForDrive(drive);

    /// <inheritdoc/>
    public void RemoveSummaryToast() => AlertToasts.RemoveSummary();

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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _pendingOpenTimer?.Stop();
        _alertsWindow?.Close();
        _settingsWindow?.Close();
        _statusWindow?.Close();
        _taskbarIcon.Dispose();
        if (!_client.DisposeAsync().AsTask().Wait(ShutdownWait))
        {
            _log.Warning("The pipe client did not stop within 2 s of exit.", null);
        }

        _iconSetter.Dispose();
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
        if (_pendingOpen is not TrayWindow window)
        {
            return;
        }

        _pendingOpen = null;
        if (window == TrayWindow.Alerts && _pendingSelectLatest)
        {
            Post(async () => (await ShowAlertsAsync(null)).SelectLatest());
        }
        else
        {
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

    // The General category is the one raised when the light or dark setting of the taskbar changes; it arrives on the
    // SystemEvents thread, so the icon is updated on the UI thread.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
        {
            return;
        }

        Post(() =>
        {
            _taskbarTheme = TaskbarThemeReader.Read();
            UpdateIcon();
        });
    }

    private void UpdateIcon()
    {
        _iconSetter.Apply(TrayIconKey.Create(_tray.State, _tray.UsedFraction, _taskbarTheme));
        _taskbarIcon.ToolTipText = _tray.ToolTipText;
    }
}
