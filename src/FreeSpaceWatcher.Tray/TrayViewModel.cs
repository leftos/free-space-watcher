using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Alerts;
using FreeSpaceWatcher.Tray.Icons;
using FreeSpaceWatcher.Tray.Pipe;
using FreeSpaceWatcher.Tray.Status;
using FreeSpaceWatcher.Tray.Toasts;

namespace FreeSpaceWatcher.Tray;

/// <summary>What the tray view model asks of the application: windows, toasts, Explorer and exiting.</summary>
public interface ITrayShell
{
    /// <summary>Opens (or activates) the alerts window and selects an alert.</summary>
    /// <param name="alertId">The alert to select, or null for none in particular.</param>
    /// <returns>The window's view model, once the list is loaded.</returns>
    Task<AlertsViewModel> ShowAlertsAsync(string? alertId);

    /// <summary>Opens (or activates) the settings window.</summary>
    void ShowSettings();

    /// <summary>Shows a toast for a new alert.</summary>
    /// <param name="alert">The alert.</param>
    void ShowAlertToast(Alert alert);

    /// <summary>Shows the summary toast for the unacknowledged alerts found on connect.</summary>
    /// <param name="count">How many alerts are unacknowledged.</param>
    void ShowSummaryToast(int count);

    /// <summary>Opens a folder in Explorer, reporting a failure to the user.</summary>
    /// <param name="folder">The folder.</param>
    void OpenFolder(string folder);

    /// <summary>Shows an error to the user.</summary>
    /// <param name="title">The title.</param>
    /// <param name="message">What went wrong.</param>
    void ShowError(string title, string message);

    /// <summary>Closes the tray app.</summary>
    void Shutdown();
}

/// <summary>The tray icon's state and tooltip, the menu commands, and what the tray does on connect, status, alerts and toast clicks.</summary>
/// <remarks>Every member is called on the UI thread.</remarks>
/// <param name="channel">The service connection.</param>
/// <param name="shell">Windows, toasts and exiting.</param>
public sealed partial class TrayViewModel(IServiceChannel channel, ITrayShell shell) : ObservableObject
{
    private StatusResponse? _status;
    private WatcherConfig? _config;

    /// <summary>Gets whether the service is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(ToolTipText))]
    public partial bool IsConnected { get; private set; }

    /// <summary>Gets how many alerts are unacknowledged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    public partial int UnacknowledgedCount { get; private set; }

    /// <summary>Gets what the icon shows.</summary>
    public TrayState State =>
        !IsConnected ? TrayState.Disconnected
        : UnacknowledgedCount > 0 ? TrayState.Alert
        : TrayState.Ok;

    /// <summary>Gets the tooltip: one line per watched drive, or "Service not running".</summary>
    public string ToolTipText => StatusText.Tooltip(IsConnected, _status, _config);

    /// <summary>Handles a connection change: on connect, loads the configuration and the unacknowledged alerts.</summary>
    /// <param name="connected">Whether the service is now connected.</param>
    /// <returns>A task that completes when the connect work is done.</returns>
    public async Task OnConnectionChangedAsync(bool connected)
    {
        if (!connected)
        {
            _status = null;
            IsConnected = false;
            return;
        }

        IsConnected = true;
        await LoadConfigAsync();
        int unacknowledged = await RefreshUnacknowledgedAsync();
        if (unacknowledged > 0)
        {
            shell.ShowSummaryToast(unacknowledged);
        }
    }

    /// <summary>Shows new status in the tooltip.</summary>
    /// <param name="status">The status.</param>
    public void OnStatus(StatusResponse status)
    {
        _status = status;
        OnPropertyChanged(nameof(ToolTipText));
    }

    /// <summary>Shows a toast for a new alert and recounts the unacknowledged alerts.</summary>
    /// <param name="alert">The alert.</param>
    /// <returns>A task that completes when the count is updated.</returns>
    public async Task OnAlertAsync(Alert alert)
    {
        shell.ShowAlertToast(alert);
        await RefreshUnacknowledgedAsync();
    }

    /// <summary>Reloads the configuration, whose noise floors decide what the tooltip shows.</summary>
    /// <returns>A task that completes when the configuration is loaded or the load failed.</returns>
    public async Task LoadConfigAsync()
    {
        ConfigResponse? response = await TrySendAsync<ConfigResponse>(new GetConfigRequest());
        if (response is not null)
        {
            _config = response.Config;
            OnPropertyChanged(nameof(ToolTipText));
        }
    }

    /// <summary>Recounts the unacknowledged alerts.</summary>
    /// <returns>The count; the previous count when the service did not answer.</returns>
    public async Task<int> RefreshUnacknowledgedAsync()
    {
        AlertListResponse? response = await TrySendAsync<AlertListResponse>(new ListAlertsRequest());
        if (response is not null)
        {
            UnacknowledgedCount = response.Alerts.Count(a => !a.Acknowledged);
        }

        return UnacknowledgedCount;
    }

    /// <summary>Carries out a toast click.</summary>
    /// <param name="request">What the toast asks for.</param>
    /// <returns>A task that completes when the request is handled.</returns>
    public async Task HandleToastAsync(ToastRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Action)
        {
            case ToastRequest.DetailsAction:
                await shell.ShowAlertsAsync(request.AlertId);
                break;
            case ToastRequest.OpenFolderAction when request.Folder is string folder:
                shell.OpenFolder(folder);
                break;
            case ToastRequest.SuspendAction when request.ProcessId is int processId:
                await SuspendFromToastAsync(request, processId);
                break;
            default:
                await shell.ShowAlertsAsync(null);
                break;
        }
    }

    private async Task SuspendFromToastAsync(ToastRequest request, int processId)
    {
        string name = request.ProcessName ?? $"pid {processId}";
        ProcessActionRequest action = new(processId, request.ProcessStartTime, ProcessAction.Suspend);
        ProcessActionResponse? response;
        try
        {
            response = await channel.SendAsync<ProcessActionResponse>(action, CancellationToken.None);
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            shell.ShowError($"Suspend {name}", ex.Message);
            return;
        }

        if (response.AccessDenied)
        {
            AlertsViewModel alerts = await shell.ShowAlertsAsync(request.AlertId);
            alerts.OfferElevation(ProcessAction.Suspend, processId, request.ProcessStartTime, name);
        }
        else if (!response.Ok)
        {
            shell.ShowError($"Suspend {name}", response.Error ?? "The service could not suspend the process.");
        }
    }

    private async Task<TResponse?> TrySendAsync<TResponse>(PipeMessage request)
        where TResponse : PipeMessage
    {
        try
        {
            return await channel.SendAsync<TResponse>(request, CancellationToken.None);
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            System.Diagnostics.Trace.TraceWarning($"{request.GetType().Name} failed: {ex.Message}");
            return null;
        }
    }

    [RelayCommand]
    private async Task ShowAlertsAsync() => await shell.ShowAlertsAsync(null);

    [RelayCommand]
    private void ShowSettings() => shell.ShowSettings();

    [RelayCommand]
    private void Exit() => shell.Shutdown();
}
