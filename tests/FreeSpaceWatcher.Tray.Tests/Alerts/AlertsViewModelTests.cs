using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Alerts;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Tests.Alerts;

public sealed class AlertsViewModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);

    private readonly FakeChannel _channel = new();
    private readonly FakeDialogs _dialogs = new();

    [Fact]
    public async Task Commands_EmptyList_AreAllDisabled()
    {
        AlertsViewModel viewModel = await LoadAsync();

        Assert.False(viewModel.AcknowledgeAllCommand.CanExecute(null));
        Assert.False(viewModel.AcknowledgeSelectedCommand.CanExecute(null));
        Assert.False(viewModel.ClearCommand.CanExecute(null));
        Assert.False(viewModel.ClearAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_AllAcknowledged_OnlyClearAllWithoutSelection()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, acknowledged: true), Summary("b", 1, acknowledged: true));

        Assert.False(viewModel.AcknowledgeAllCommand.CanExecute(null));
        Assert.False(viewModel.AcknowledgeSelectedCommand.CanExecute(null));
        Assert.False(viewModel.ClearCommand.CanExecute(null));
        Assert.True(viewModel.ClearAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Commands_Mixed_FollowTheSelection()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("acked", 0, acknowledged: true), Summary("open", 1, acknowledged: false));
        AlertListItem acked = viewModel.Alerts.Single(a => a.Summary.Id == "acked");
        AlertListItem open = viewModel.Alerts.Single(a => a.Summary.Id == "open");

        Assert.True(viewModel.AcknowledgeAllCommand.CanExecute(null));
        Assert.False(viewModel.ClearCommand.CanExecute(null));

        acked.IsSelected = true;
        Assert.True(viewModel.ClearCommand.CanExecute(null));
        Assert.False(viewModel.AcknowledgeSelectedCommand.CanExecute(null));

        open.IsSelected = true;
        Assert.True(viewModel.AcknowledgeSelectedCommand.CanExecute(null));

        acked.IsSelected = false;
        open.IsSelected = false;
        Assert.False(viewModel.ClearCommand.CanExecute(null));
        Assert.False(viewModel.AcknowledgeSelectedCommand.CanExecute(null));
    }

    [Fact]
    public void ClearQuestions_OneSelected_ManySelected_All()
    {
        Assert.Null(AlertsViewModel.ClearQuestion(1));
        Assert.Equal("Delete 3 alerts from history?", AlertsViewModel.ClearQuestion(3));
        Assert.Equal("Delete all 4 alerts from history? This cannot be undone.", AlertsViewModel.ClearAllQuestion(4));
        Assert.Equal("Delete 1 alert from history? This cannot be undone.", AlertsViewModel.ClearAllQuestion(1));
    }

    [Fact]
    public async Task Clear_OneSelected_DeletesWithoutAsking()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, acknowledged: false), Summary("b", 1, acknowledged: false));
        viewModel.Alerts.Single(a => a.Summary.Id == "a").IsSelected = true;

        await viewModel.ClearCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Questions);
        string[] expected = ["a"];
        Assert.Equal(expected, Assert.Single(_channel.Sent.OfType<DeleteAlertsRequest>()).Ids);
    }

    [Fact]
    public async Task Clear_ManySelected_AsksWithTheCount_AndNoKeepsThem()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, acknowledged: false), Summary("b", 1, acknowledged: false));
        foreach (AlertListItem item in viewModel.Alerts)
        {
            item.IsSelected = true;
        }

        _dialogs.Answer = false;
        await viewModel.ClearCommand.ExecuteAsync(null);

        Assert.Equal("Delete 2 alerts from history?", Assert.Single(_dialogs.Questions));
        Assert.Empty(_channel.Sent.OfType<DeleteAlertsRequest>());
    }

    [Fact]
    public async Task ClearAll_AsksWithTheCount_AndDeletesEverything()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, acknowledged: false), Summary("b", 1, acknowledged: true));

        await viewModel.ClearAllCommand.ExecuteAsync(null);

        Assert.Equal("Delete all 2 alerts from history? This cannot be undone.", Assert.Single(_dialogs.Questions));
        Assert.Null(Assert.Single(_channel.Sent.OfType<DeleteAlertsRequest>()).Ids);
    }

    [Fact]
    public async Task AcknowledgeAll_NeverAsks_AndSendsNullIds()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, acknowledged: false));

        await viewModel.AcknowledgeAllCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Questions);
        Assert.Null(Assert.Single(_channel.Sent.OfType<AckAlertsRequest>()).Ids);
    }

    [Fact]
    public async Task Refresh_KeepsTheSelectedRowsThatStillExist_AndTheShownAlert()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, false), Summary("b", 1, false), Summary("c", 2, false));
        viewModel.Alerts.Single(a => a.Summary.Id == "a").IsSelected = true;
        viewModel.Alerts.Single(a => a.Summary.Id == "b").IsSelected = true;

        _channel.Alerts = [Summary("b", 1, true), Summary("c", 2, false)];
        await viewModel.SelectAlertAsync(null);

        string[] expected = ["b"];
        Assert.Equal(expected, viewModel.SelectedAlerts.Select(a => a.Summary.Id));
        Assert.Equal("b", viewModel.SelectedAlert?.Summary.Id);
        Assert.Equal("b", viewModel.Details?.Id);
        Assert.True(viewModel.Details?.Acknowledged);
    }

    [Fact]
    public async Task Refresh_ShownAlertDeleted_ClearsTheDetails()
    {
        AlertsViewModel viewModel = await LoadAsync(Summary("a", 0, false), Summary("b", 1, false));
        viewModel.Alerts.Single(a => a.Summary.Id == "b").IsSelected = true;
        viewModel.Alerts.Single(a => a.Summary.Id == "a").IsSelected = true;
        Assert.Equal("a", viewModel.Details?.Id);

        _channel.Alerts = [Summary("b", 1, false)];
        await viewModel.SelectAlertAsync(null);

        string[] expected = ["b"];
        Assert.Equal(expected, viewModel.SelectedAlerts.Select(a => a.Summary.Id));
        Assert.Null(viewModel.SelectedAlert);
        Assert.Null(viewModel.Details);
    }

    private async Task<AlertsViewModel> LoadAsync(params AlertSummary[] alerts)
    {
        _channel.Alerts = alerts;
        AlertsViewModel viewModel = new(_channel, _dialogs, new FakeShell(), new FakeTrayLog());
        await viewModel.SelectAlertAsync(null);
        return viewModel;
    }

    private static AlertSummary Summary(string id, int minute, bool acknowledged) =>
        new()
        {
            Id = id,
            Time = T0.AddMinutes(minute),
            Drive = "C",
            Trigger = TriggerKind.Floor,
            Reason = "C: below the floor",
            Acknowledged = acknowledged,
        };

    private static Alert FullAlert(AlertSummary summary) =>
        new()
        {
            Id = summary.Id,
            Time = summary.Time,
            Drive = summary.Drive,
            Trigger = summary.Trigger,
            IsEscalation = false,
            Reason = summary.Reason,
            FreeBytes = 1L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 0,
            TimeToFull = null,
            UnattributedBytes = 0,
            Processes = [],
            Acknowledged = summary.Acknowledged,
        };

    private sealed class FakeChannel : IServiceChannel
    {
        public AlertSummary[] Alerts { get; set; } = [];

        public List<PipeMessage> Sent { get; } = [];

        public bool IsConnected => true;

        public Task<TResponse> SendAsync<TResponse>(PipeMessage request, CancellationToken cancellationToken)
            where TResponse : PipeMessage
        {
            Sent.Add(request);
            PipeMessage response = request switch
            {
                ListAlertsRequest => new AlertListResponse(Alerts),
                GetAlertRequest get => new AlertResponse(Alerts.FirstOrDefault(a => a.Id == get.Id) is { } found ? FullAlert(found) : null),
                AckAlertsRequest ack => new AckResponse(ack.Ids?.Count ?? Alerts.Length),
                DeleteAlertsRequest delete => new DeleteAlertsResponse(delete.Ids?.Count ?? Alerts.Length),
                _ => throw new InvalidOperationException($"Unexpected {request}"),
            };
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class FakeDialogs : IUserDialogs
    {
        public bool Answer { get; set; } = true;

        public List<string> Questions { get; } = [];

        public bool Confirm(string title, string message)
        {
            Questions.Add(message);
            return Answer;
        }

        public void ShowError(string title, string message) => throw new InvalidOperationException($"Unexpected error: {message}");
    }

    private sealed class FakeShell : IShellActions
    {
        public string? OpenFolder(string folder) => throw new InvalidOperationException("Unexpected OpenFolder");

        public string? ShowFile(string file) => throw new InvalidOperationException("Unexpected ShowFile");

        public string? RunElevated(ProcessAction action, int processId, DateTimeOffset? processStartTime) =>
            throw new InvalidOperationException("Unexpected RunElevated");
    }
}
