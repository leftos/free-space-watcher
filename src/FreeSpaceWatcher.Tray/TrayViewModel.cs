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

    /// <summary>Opens (or activates) the status window.</summary>
    void ShowStatus();

    /// <summary>Shows a toast for a new alert.</summary>
    /// <param name="alert">The alert.</param>
    void ShowAlertToast(Alert alert);

    /// <summary>Replaces a resolved alert's toast with the quiet resolved toast.</summary>
    /// <param name="alert">The resolved alert.</param>
    void ShowResolvedToast(Alert alert);

    /// <summary>Shows the summary toast for the unacknowledged alerts found on connect.</summary>
    /// <param name="count">How many alerts are unacknowledged.</param>
    void ShowSummaryToast(int count);

    /// <summary>Shows the outcome of a process action started from a toast.</summary>
    /// <param name="outcome">The outcome.</param>
    void ShowProcessActionToast(ProcessActionToast outcome);

    /// <summary>Removes every alert toast from Action Center.</summary>
    void RemoveAlertToasts();

    /// <summary>Removes one drive's alert toast from Action Center.</summary>
    /// <param name="drive">The drive letter the toast is tagged with.</param>
    void RemoveAlertToast(string drive);

    /// <summary>Removes the summary toast from Action Center.</summary>
    void RemoveSummaryToast();

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
/// <param name="log">Receives failed requests and process actions started from toasts.</param>
public sealed partial class TrayViewModel(IServiceChannel channel, ITrayShell shell, ITrayLog log) : ObservableObject
{
    private readonly HashSet<string> _resolvedToastDrives = new(StringComparer.OrdinalIgnoreCase);
    private StatusResponse? _status;
    private WatcherConfig? _config;
    private IReadOnlyList<AlertSummary>? _alerts;

    /// <summary>Gets whether the service is connected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State), nameof(ToolTipText))]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeAllCommand))]
    public partial bool IsConnected { get; private set; }

    /// <summary>Gets how many alerts are unacknowledged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeAllCommand))]
    public partial int UnacknowledgedCount { get; private set; }

    /// <summary>Gets the latest status, or null while disconnected or before the first one arrives.</summary>
    public StatusResponse? Status => _status;

    /// <summary>Gets the configuration in use, or null until it is loaded.</summary>
    public WatcherConfig? Config => _config;

    /// <summary>Gets the alert list, or null until it is loaded.</summary>
    public IReadOnlyList<AlertSummary>? Alerts => _alerts;

    /// <summary>Gets what the icon shows.</summary>
    public TrayState State =>
        !IsConnected ? TrayState.Disconnected
        : UnacknowledgedCount > 0 ? TrayState.Alert
        : TrayState.Ok;

    /// <summary>Gets the used fraction, from 0 to 1, of the fullest watched drive that is available; 0 without status.</summary>
    /// <remarks>New status raises <see cref="ToolTipText"/>'s change, which is when the icon reads this again.</remarks>
    public double UsedFraction =>
        _status
            ?.Drives.Where(d => d.Watched && d.Available && d.TotalBytes > 0)
            .Select(d => Math.Clamp((double)(d.TotalBytes - d.FreeBytes) / d.TotalBytes, 0, 1))
            .DefaultIfEmpty(0)
            .Max()
        ?? 0;

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
        ArgumentNullException.ThrowIfNull(alert);
        _resolvedToastDrives.Remove(alert.Drive);
        shell.ShowAlertToast(alert);
        await RefreshUnacknowledgedAsync();
    }

    /// <summary>
    /// Replaces the drive's alert toast with the resolved toast, and keeps that toast through the alert changes the resolution
    /// pushes next, until a new alert on the drive replaces it.
    /// </summary>
    /// <param name="alert">The resolved alert.</param>
    public void OnAlertResolved(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        shell.ShowResolvedToast(alert);
        _resolvedToastDrives.Add(alert.Drive);
    }

    /// <summary>
    /// Recounts the unacknowledged alerts after alerts were acknowledged, deleted or resolved, and removes the alert toasts that no
    /// longer apply: all of them when nothing is unacknowledged, otherwise the toast of each drive that has no unacknowledged alert
    /// left, whether its alerts were acknowledged or deleted. A drive whose toast is a resolved toast keeps it.
    /// </summary>
    /// <returns>A task that completes when the count and the toasts are updated.</returns>
    public async Task OnAlertsChangedAsync()
    {
        IReadOnlyList<AlertSummary>? before = _alerts;
        int unacknowledged = await RefreshUnacknowledgedAsync();
        if (_alerts is not { } after || ReferenceEquals(before, after))
        {
            return;
        }

        if (unacknowledged == 0 && _resolvedToastDrives.Count == 0)
        {
            shell.RemoveAlertToasts();
            return;
        }

        if (unacknowledged == 0)
        {
            shell.RemoveSummaryToast();
        }

        if (before is null)
        {
            return;
        }

        foreach (string drive in AlertToasts.DrivesToClear(before, after).Where(d => !_resolvedToastDrives.Contains(d)))
        {
            shell.RemoveAlertToast(drive);
        }
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
            _alerts = response.Alerts;
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
                await ActFromToastAsync(request, processId, ProcessAction.Suspend);
                break;
            case ToastRequest.ResumeAction when request.ProcessId is int processId:
                await ActFromToastAsync(request, processId, ProcessAction.Resume);
                break;
            default:
                await shell.ShowAlertsAsync(null);
                break;
        }
    }

    private async Task ActFromToastAsync(ToastRequest request, int processId, ProcessAction action)
    {
        string name = request.ProcessName ?? ProcessActionText.UnknownName;
        ProcessActionRequest message = new(processId, request.ProcessStartTime, action);
        string? error;
        try
        {
            ProcessActionResponse response = await ProcessActionSender.SendAsync(channel, message, name, log);
            if (response.AccessDenied)
            {
                AlertsViewModel alerts = await shell.ShowAlertsAsync(request.AlertId);
                await alerts.OfferElevationAsync(action, processId, request.ProcessStartTime, name);
                return;
            }

            error = ProcessActionText.ErrorOf(response);
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            error = ex.Message;
        }

        shell.ShowProcessActionToast(
            new ProcessActionToast
            {
                Action = action,
                ProcessId = processId,
                ProcessStartTime = request.ProcessStartTime,
                ProcessName = name,
                AlertId = request.AlertId,
                Error = error,
            }
        );
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
            log.Warning($"{request.GetType().Name} failed.", ex);
            return null;
        }
    }

    private bool CanAcknowledgeAll() => IsConnected && UnacknowledgedCount > 0;

    [RelayCommand(CanExecute = nameof(CanAcknowledgeAll))]
    private async Task AcknowledgeAllAsync() => await TrySendAsync<AckResponse>(new AckAlertsRequest(null));

    [RelayCommand]
    private async Task ShowAlertsAsync() => await shell.ShowAlertsAsync(null);

    [RelayCommand]
    private void ShowSettings() => shell.ShowSettings();

    [RelayCommand]
    private void ShowStatus() => shell.ShowStatus();

    [RelayCommand]
    private void Exit() => shell.Shutdown();
}
