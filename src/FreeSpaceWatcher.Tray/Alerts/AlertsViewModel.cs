using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>
/// The alerts window: history newest first, the selected alert's details with each process's state, process actions with the
/// outcome of the last one, and acknowledgement.
/// </summary>
/// <param name="channel">The service connection.</param>
/// <param name="dialogs">Confirmations and error messages.</param>
/// <param name="shell">Explorer and the elevation helper.</param>
public sealed partial class AlertsViewModel(IServiceChannel channel, IUserDialogs dialogs, IShellActions shell) : ObservableObject
{
    private string? _statusAlertId;

    /// <summary>Raised after an alert was acknowledged, so the tray can recount unacknowledged alerts.</summary>
    public event EventHandler? AlertsChanged;

    /// <summary>Gets the alert history, newest first.</summary>
    public ObservableCollection<AlertListItem> Alerts { get; } = [];

    /// <summary>Gets or sets the selected alert; selecting one loads its details.</summary>
    [ObservableProperty]
    public partial AlertListItem? SelectedAlert { get; set; }

    /// <summary>Gets the selected alert's details.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcknowledgeCommand))]
    public partial AlertDetails? Details { get; private set; }

    /// <summary>Gets the last failure to show above the list, or null.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Gets the outcome of the last process action, shown under the details; cleared when another alert is selected.</summary>
    [ObservableProperty]
    public partial string? ActionStatus { get; private set; }

    /// <summary>Gets whether <see cref="ActionStatus"/> reports a failure.</summary>
    [ObservableProperty]
    public partial bool ActionFailed { get; private set; }

    /// <summary>Reloads the history and selects an alert.</summary>
    /// <param name="alertId">The alert to select, or null to keep the current selection.</param>
    /// <returns>A task that completes when the list is loaded and the selection set.</returns>
    public async Task SelectAlertAsync(string? alertId)
    {
        await RefreshAsync();
        if (alertId is not null)
        {
            SelectedAlert = Alerts.FirstOrDefault(a => a.Summary.Id == alertId);
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
            response = await ProcessActionSender.SendAsync(channel, request, processName);
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
        if (response is null)
        {
            return;
        }

        string? selectedId = SelectedAlert?.Summary.Id;
        Alerts.Clear();
        foreach (AlertSummary summary in response.Alerts.OrderByDescending(a => a.Time))
        {
            Alerts.Add(new AlertListItem(summary));
        }

        SelectedAlert = selectedId is null ? null : Alerts.FirstOrDefault(a => a.Summary.Id == selectedId);
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

        Details = response.Alert is null ? null : AlertDetails.From(response.Alert);
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

    private bool CanAcknowledge() => Details is { Acknowledged: false };

    [RelayCommand(CanExecute = nameof(CanAcknowledge))]
    private async Task AcknowledgeAsync()
    {
        if (Details is not { } details)
        {
            return;
        }

        AckResponse? response = await SendAsync<AckResponse>(new AckAlertRequest(details.Id));
        if (response is null)
        {
            return;
        }

        if (!response.Ok)
        {
            ErrorMessage = $"Alert {details.Id} is no longer in the history.";
        }

        await RefreshAsync();
        AlertsChanged?.Invoke(this, EventArgs.Empty);
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
