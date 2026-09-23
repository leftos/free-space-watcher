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
using FreeSpaceWatcher.Tray.Toasts;
using H.NotifyIcon;
using Microsoft.Toolkit.Uwp.Notifications;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray;

/// <summary>Wires the tray: the notify icon, the pipe client's events onto the UI thread, the windows and the toasts.</summary>
public sealed class TrayHost : ITrayShell, IDisposable
{
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(2);
    private readonly Dispatcher _dispatcher;
    private readonly PipeClient _client = new(PipeProtocol.PipeName, PipeClient.DefaultRequestTimeout);
    private readonly MessageBoxDialogs _dialogs = new();
    private readonly ShellActions _shellActions = new(ShellActions.DefaultElevateHelperPath);
    private readonly Dictionary<TrayState, DrawingIcon> _icons;
    private readonly TrayViewModel _tray;
    private readonly TaskbarIcon _taskbarIcon;
    private AlertsWindow? _alertsWindow;
    private SettingsWindow? _settingsWindow;

    /// <summary>Initializes the host; nothing is shown or connected until <see cref="Start"/>.</summary>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    public TrayHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _icons = Enum.GetValues<TrayState>().ToDictionary(s => s, s => TrayIconRenderer.ToIcon(TrayIconRenderer.Render(s)));
        _tray = new TrayViewModel(_client, this);
        _taskbarIcon = CreateTaskbarIcon();
    }

    /// <summary>Shows the icon, listens for toast clicks and starts connecting.</summary>
    public void Start()
    {
        _tray.PropertyChanged += OnTrayPropertyChanged;
        _client.ConnectionChanged += (_, connected) => Post(() => OnConnectionChangedAsync(connected));
        _client.StatusReceived += (_, status) => Post(() => OnStatus(status));
        _client.AlertReceived += (_, alert) => Post(() => OnAlertAsync(alert));
        _client.AlertsChanged += (_, _) => Post(OnAlertsChangedAsync);
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);
        UpdateIcon();
        _client.Start();
    }

    /// <inheritdoc/>
    public async Task<AlertsViewModel> ShowAlertsAsync(string? alertId)
    {
        if (_alertsWindow?.DataContext is not AlertsViewModel viewModel)
        {
            viewModel = new AlertsViewModel(_client, _dialogs, _shellActions);
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
        _alertsWindow?.Close();
        _settingsWindow?.Close();
        _taskbarIcon.Dispose();
        if (!_client.DisposeAsync().AsTask().Wait(ShutdownWait))
        {
            TrayLog.Warning("The pipe client did not stop within 2 s of exit.", null);
        }

        foreach (DrawingIcon icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private TaskbarIcon CreateTaskbarIcon()
    {
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "Acknowledge all", Command = _tray.AcknowledgeAllCommand });
        menu.Items.Add(new MenuItem { Header = "Alerts…", Command = _tray.ShowAlertsCommand });
        menu.Items.Add(new MenuItem { Header = "Settings…", Command = _tray.ShowSettingsCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Exit", Command = _tray.ExitCommand });
        return new TaskbarIcon
        {
            ContextMenu = menu,
            LeftClickCommand = _tray.ShowAlertsCommand,
            NoLeftClickDelay = true,
            ToolTipText = _tray.ToolTipText,
            Icon = _icons[_tray.State],
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

    private void OnTrayPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateIcon();

    private void UpdateIcon()
    {
        _taskbarIcon.Icon = _icons[_tray.State];
        _taskbarIcon.ToolTipText = _tray.ToolTipText;
    }
}
