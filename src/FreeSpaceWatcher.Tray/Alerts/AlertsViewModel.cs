using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>
/// The alerts window: history newest first with a multi-row selection, the details of the row selected last with each process's
/// state, process actions with the outcome of the last one, acknowledgement, and deleting alerts from the history.
/// </summary>
/// <param name="channel">The service connection.</param>
/// <param name="dialogs">Confirmations and error messages.</param>
/// <param name="shell">Explorer and the elevation helper.</param>
/// <param name="log">Receives each process action and its outcome.</param>
/// <param name="clock">The current time and time zone, for the rows' relative times and the details' absolute time.</param>
public sealed partial class AlertsViewModel(IServiceChannel channel, IUserDialogs dialogs, IShellActions shell, ITrayLog log, TimeProvider clock)
    : ObservableObject
{
    private string? _statusAlertId;

    /// <summary>Gets the alert history, newest first; each row's <see cref="AlertListItem.IsSelected"/> is the list's selection.</summary>
    public ObservableCollection<AlertListItem> Alerts { get; } = [];

    /// <summary>Gets whether the history is empty, which shows the list's empty state.</summary>
    public bool IsEmpty => Alerts.Count == 0;

    /// <summary>Gets the selected rows, in list order.</summary>
    public IReadOnlyList<AlertListItem> SelectedAlerts => [.. Alerts.Where(a => a.IsSelected)];

    /// <summary>Gets the row whose details are shown: the row selected last, while it stays selected; loading it loads its details.</summary>
    [ObservableProperty]
    public partial AlertListItem? SelectedAlert { get; private set; }

    /// <summary>Gets the selected alert's details.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand))]
    public partial AlertDetails? Details { get; private set; }

    /// <summary>Gets the last failure to show above the list, or null.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Gets the outcome of the last process action, shown above the details; cleared when another alert is selected.</summary>
    [ObservableProperty]
    public partial string? ActionStatus { get; private set; }

    /// <summary>Gets whether <see cref="ActionStatus"/> reports a failure.</summary>
    [ObservableProperty]
    public partial bool ActionFailed { get; private set; }

    /// <summary>Asks whether to clear the selected alerts: null (no question) for a single alert, otherwise "Delete N alerts from history?".</summary>
    /// <param name="count">How many alerts are selected.</param>
    /// <returns>The question, or null when clearing goes ahead without one.</returns>
    public static string? ClearQuestion(int count) => count > 1 ? $"Delete {count} alerts from history?" : null;

    /// <summary>Asks whether to clear every alert in the history.</summary>
    /// <param name="count">How many alerts the list shows.</param>
    /// <returns>The question, e.g. "Delete all 3 alerts from history? This cannot be undone.".</returns>
    public static string ClearAllQuestion(int count) =>
        count == 1 ? "Delete 1 alert from history? This cannot be undone." : $"Delete all {count} alerts from history? This cannot be undone.";

    /// <summary>Reloads the history, keeping the selected rows that still exist, and optionally selects just one alert.</summary>
    /// <param name="alertId">The alert to select alone, or null to keep the current selection.</param>
    /// <returns>A task that completes when the list is loaded and the selection set.</returns>
    public async Task SelectAlertAsync(string? alertId)
    {
        await RefreshAsync();
        if (alertId is not null)
        {
            foreach (AlertListItem item in Alerts)
            {
                item.IsSelected = item.Summary.Id == alertId;
            }
        }
    }

    /// <summary>Selects the newest alert alone, which shows its details; does nothing while the list is empty.</summary>
    public void SelectLatest()
    {
        if (Alerts.Count == 0)
        {
            return;
        }

        foreach (AlertListItem item in Alerts)
        {
            item.IsSelected = ReferenceEquals(item, Alerts[0]);
        }
    }

    /// <summary>
    /// Asks the service to act on a process and shows the outcome in the status line; when access is denied, offers to retry as
    /// administrator. The process states are refreshed afterwards.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="processStartTime">The process start time, when known.</param>
    /// <param name="processName">The process name, for messages.</param>
    /// <returns>A task that completes when the action and any retry offer are done.</returns>
    public async Task RunProcessActionAsync(ProcessAction action, int processId, DateTimeOffset? processStartTime, string processName)
    {
        string? alertId = SelectedAlert?.Summary.Id;
        ProcessActionRequest request = new(processId, processStartTime, action);
        ProcessActionResponse response;
        try
        {
            response = await ProcessActionSender.SendAsync(channel, request, processName, log);
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            ShowActionStatus(alertId, ProcessActionText.StatusLine(action, processName, processId, ex.Message), failed: true);
            return;
        }

        ApplyState(processId, response.State);
        if (response.AccessDenied)
        {
            await OfferElevationAsync(action, processId, processStartTime, processName);
            return;
        }

        string? error = ProcessActionText.ErrorOf(response);
        ShowActionStatus(alertId, ProcessActionText.StatusLine(action, processName, processId, error), failed: error is not null);
        await RefreshProcessStatesAsync();
    }

    /// <summary>
    /// Offers "Retry as administrator" for an action the service was denied, and runs the elevation helper on yes; a refusal or
    /// the helper's error goes to the status line, and the process states are refreshed after the helper ran.
    /// </summary>
    /// <param name="action">The action.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="processStartTime">The process start time, when known.</param>
    /// <param name="processName">The process name, for the question.</param>
    /// <returns>A task that completes when the offer, the helper and the state refresh are done.</returns>
    public async Task OfferElevationAsync(ProcessAction action, int processId, DateTimeOffset? processStartTime, string processName)
    {
        string question = $"Access to {processName} (pid {processId}) was denied. Retry as administrator?";
        if (!dialogs.Confirm($"{action} {processName}", question))
        {
            ShowActionStatus(SelectedAlert?.Summary.Id, ProcessActionText.Denied(processName, processId), failed: true);
            return;
        }

        string? error = shell.RunElevated(action, processId, processStartTime);
        ShowActionStatus(SelectedAlert?.Summary.Id, error, failed: error is not null);
        await RefreshProcessStatesAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        AlertListResponse? response = await SendAsync<AlertListResponse>(new ListAlertsRequest());
        if (response is not null)
        {
            ShowAlerts(response.Alerts);
        }
    }

    private void ShowAlerts(IReadOnlyList<AlertSummary> summaries)
    {
        HashSet<string> selectedIds = [.. SelectedAlerts.Select(a => a.Summary.Id)];
        string? shownId = SelectedAlert?.Summary.Id;
        foreach (AlertListItem old in Alerts)
        {
            old.PropertyChanged -= OnItemPropertyChanged;
        }

        Alerts.Clear();
        DateTimeOffset now = clock.GetUtcNow();
        foreach (AlertSummary summary in summaries.OrderByDescending(a => a.Time))
        {
            AlertListItem item = new(summary, now, clock.LocalTimeZone) { IsSelected = selectedIds.Contains(summary.Id) };
            item.PropertyChanged += OnItemPropertyChanged;
            Alerts.Add(item);
        }

        SelectedAlert = shownId is null ? null : Alerts.FirstOrDefault(a => a.Summary.Id == shownId);
        OnPropertyChanged(nameof(IsEmpty));
        NotifyListCommands();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AlertListItem item || e.PropertyName != nameof(AlertListItem.IsSelected))
        {
            return;
        }

        if (item.IsSelected)
        {
            SelectedAlert = item;
        }
        else if (ReferenceEquals(item, SelectedAlert))
        {
            SelectedAlert = Alerts.FirstOrDefault(a => a.IsSelected);
        }

        NotifyListCommands();
    }

    private void NotifyListCommands()
    {
        AcknowledgeAllCommand.NotifyCanExecuteChanged();
        AcknowledgeSelectedCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAlertChanged(AlertListItem? value)
    {
        if (value is not null && value.Summary.Id != _statusAlertId)
        {
            ActionStatus = null;
            ActionFailed = false;
        }

        _ = LoadDetailsAsync(value?.Summary.Id);
    }

    private async Task LoadDetailsAsync(string? alertId)
    {
        if (alertId is null)
        {
            Details = null;
            return;
        }

        AlertResponse? response = await SendAsync<AlertResponse>(new GetAlertRequest(alertId));
        if (response is null || SelectedAlert?.Summary.Id != alertId)
        {
            return;
        }

        Details = response.Alert is null ? null : AlertDetails.From(response.Alert, clock.LocalTimeZone);
        if (response.Alert is null)
        {
            ErrorMessage = $"Alert {alertId} is no longer in the history.";
            return;
        }

        await RefreshProcessStatesAsync();
    }

    private async Task RefreshProcessStatesAsync()
    {
        if (Details is not { Processes.Count: > 0 } details)
        {
            return;
        }

        int[] processIds = [.. details.Processes.Select(p => p.Report.ProcessId).Distinct()];
        ProcessStatesResponse? response = await SendAsync<ProcessStatesResponse>(new GetProcessStatesRequest(processIds));
        if (response is null)
        {
            return;
        }

        foreach (ProcessNode node in details.Processes)
        {
            node.State = response.States.TryGetValue(node.Report.ProcessId, out ProcessState state) ? state : null;
        }
    }

    private void ApplyState(int processId, ProcessState? state)
    {
        if (state is null || Details is null)
        {
            return;
        }

        foreach (ProcessNode node in Details.Processes.Where(p => p.Report.ProcessId == processId))
        {
            node.State = state;
        }
    }

    private void ShowActionStatus(string? alertId, string? text, bool failed)
    {
        if (SelectedAlert?.Summary.Id != alertId)
        {
            return;
        }

        _statusAlertId = alertId;
        ActionStatus = text;
        ActionFailed = failed;
    }

    [RelayCommand]
    private void DismissActionStatus()
    {
        ActionStatus = null;
        ActionFailed = false;
    }

    private bool CanAcknowledge() => Details is { Acknowledged: false };

    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private Task AcknowledgeAsync() => Details is { } details ? AcknowledgeIdsAsync([details.Id]) : Task.CompletedTask;

    private bool CanAcknowledgeAll() => Alerts.Any(a => a.IsUnacknowledged);

    [RelayCommand(CanExecute = nameof(CanAcknowledgeAll))]
    private Task AcknowledgeAllAsync() => AcknowledgeIdsAsync(null);

    private bool CanAcknowledgeSelected() => Alerts.Any(a => a.IsSelected && a.IsUnacknowledged);

    [RelayCommand(CanExecute = nameof(CanAcknowledgeSelected))]
    private Task AcknowledgeSelectedAsync() => AcknowledgeIdsAsync([.. SelectedAlerts.Select(a => a.Summary.Id)]);

    private bool CanClear() => Alerts.Any(a => a.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAsync()
    {
        string[] ids = [.. SelectedAlerts.Select(a => a.Summary.Id)];
        if (ClearQuestion(ids.Length) is string question && !dialogs.Confirm("Clear alerts", question))
        {
            return;
        }

        await DeleteIdsAsync(ids);
    }

    private bool CanClearAll() => Alerts.Count > 0;

    [RelayCommand(CanExecute = nameof(CanClearAll))]
    private async Task ClearAllAsync()
    {
        if (dialogs.Confirm("Clear all alerts", ClearAllQuestion(Alerts.Count)))
        {
            await DeleteIdsAsync(null);
        }
    }

    private async Task AcknowledgeIdsAsync(IReadOnlyList<string>? ids)
    {
        if (await SendAsync<AckResponse>(new AckAlertsRequest(ids)) is not null)
        {
            await RefreshAsync();
        }
    }

    private async Task DeleteIdsAsync(IReadOnlyList<string>? ids)
    {
        if (await SendAsync<DeleteAlertsResponse>(new DeleteAlertsRequest(ids)) is not null)
        {
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private Task SuspendAsync(ProcessNode node) => RunOnNodeAsync(node, ProcessAction.Suspend);

    [RelayCommand]
    private Task ResumeAsync(ProcessNode node) => RunOnNodeAsync(node, ProcessAction.Resume);

    [RelayCommand]
    private Task KillAsync(ProcessNode node)
    {
        string question = $"Kill {node.Report.Name} (pid {node.Report.ProcessId})? Anything it has not saved is lost.";
        return dialogs.Confirm("Kill process", question) ? RunOnNodeAsync(node, ProcessAction.Kill) : Task.CompletedTask;
    }

    [RelayCommand]
    private void OpenFolder(string folder) => ShowShellError(shell.OpenFolder(folder));

    [RelayCommand]
    private void ShowFile(FileNode node) => ShowShellError(node.IsFolded ? shell.OpenFolder(node.FolderPath) : shell.ShowFile(node.File.Path));

    private Task RunOnNodeAsync(ProcessNode node, ProcessAction action) =>
        RunProcessActionAsync(action, node.Report.ProcessId, node.Report.StartTime, node.Report.Name);

    private void ShowShellError(string? error)
    {
        if (error is not null)
        {
            ErrorMessage = error;
        }
    }

    private async Task<TResponse?> SendAsync<TResponse>(PipeMessage request)
        where TResponse : PipeMessage
    {
        try
        {
            TResponse response = await channel.SendAsync<TResponse>(request, CancellationToken.None);
            ErrorMessage = null;
            return response;
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            ErrorMessage = ex.Message;
            return null;
        }
    }
}
