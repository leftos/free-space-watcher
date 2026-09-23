using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Alerts;
using FreeSpaceWatcher.Tray.Pipe;
using FreeSpaceWatcher.Tray.Toasts;
using static FreeSpaceWatcher.Tray.Tests.Toasts.AlertToastsTests;

namespace FreeSpaceWatcher.Tray.Tests;

public sealed class TrayViewModelTests
{
    private readonly FakeChannel _channel = new();
    private readonly FakeShell _shell = new();
    private readonly FakeTrayLog _log = new();

    [Fact]
    public async Task OnAlertsChanged_DriveLostItsLastUnacknowledgedAlert_RemovesThatDrivesToast()
    {
        TrayViewModel tray = await LoadAsync(Summary("c1", "C", false), Summary("d1", "D", false));

        _channel.Alerts = [Summary("d1", "D", false)];
        await tray.OnAlertsChangedAsync();

        Assert.Equal(["C"], _shell.RemovedDrives);
        Assert.Equal(0, _shell.RemoveAllCalls);
        Assert.Equal(1, tray.UnacknowledgedCount);
    }

    [Fact]
    public async Task OnAlertsChanged_CAcknowledged_WhileDStaysUnacknowledged_RemovesOnlyCsToast()
    {
        TrayViewModel tray = await LoadAsync(Summary("c1", "C", false), Summary("d1", "D", false));

        _channel.Alerts = [Summary("c1", "C", true), Summary("d1", "D", false)];
        await tray.OnAlertsChangedAsync();

        Assert.Equal(["C"], _shell.RemovedDrives);
        Assert.Equal(0, _shell.RemoveAllCalls);
        Assert.Equal(1, tray.UnacknowledgedCount);
    }

    [Fact]
    public async Task OnAlertsChanged_DriveStillHasAnUnacknowledgedAlert_KeepsItsToast()
    {
        TrayViewModel tray = await LoadAsync(Summary("c1", "C", false), Summary("c2", "C", false));

        _channel.Alerts = [Summary("c2", "C", false)];
        await tray.OnAlertsChangedAsync();

        Assert.Empty(_shell.RemovedDrives);
        Assert.Equal(0, _shell.RemoveAllCalls);
    }

    [Fact]
    public async Task OnAlertsChanged_NothingUnacknowledgedLeft_RemovesTheWholeGroup()
    {
        TrayViewModel tray = await LoadAsync(Summary("c1", "C", false), Summary("d1", "D", false));

        _channel.Alerts = [Summary("c1", "C", true)];
        await tray.OnAlertsChangedAsync();

        Assert.Equal(1, _shell.RemoveAllCalls);
        Assert.Empty(_shell.RemovedDrives);
        Assert.Equal(0, tray.UnacknowledgedCount);
    }

    [Fact]
    public async Task OnAlertsChanged_NoEarlierList_RemovesNothingPerDrive()
    {
        TrayViewModel tray = new(_channel, _shell, _log);

        _channel.Alerts = [Summary("d1", "D", false)];
        await tray.OnAlertsChangedAsync();

        Assert.Empty(_shell.RemovedDrives);
        Assert.Equal(0, _shell.RemoveAllCalls);
    }

    [Fact]
    public async Task OnAlertsChanged_ListFails_LogsIt_AndKeepsTheToastsAndTheCount()
    {
        TrayViewModel tray = await LoadAsync(Summary("c1", "C", false));

        _channel.Failure = new IOException("The connection to the FreeSpaceWatcher service was lost.");
        await tray.OnAlertsChangedAsync();

        Assert.Equal(["WARN ListAlertsRequest failed."], _log.Lines);
        Assert.Empty(_shell.RemovedDrives);
        Assert.Equal(0, _shell.RemoveAllCalls);
        Assert.Equal(1, tray.UnacknowledgedCount);
    }

    private async Task<TrayViewModel> LoadAsync(params AlertSummary[] alerts)
    {
        _channel.Alerts = alerts;
        TrayViewModel tray = new(_channel, _shell, _log);
        await tray.RefreshUnacknowledgedAsync();
        return tray;
    }

    private sealed class FakeChannel : IServiceChannel
    {
        public AlertSummary[] Alerts { get; set; } = [];

        public Exception? Failure { get; set; }

        public bool IsConnected => true;

        public Task<TResponse> SendAsync<TResponse>(PipeMessage request, CancellationToken cancellationToken)
            where TResponse : PipeMessage
        {
            if (Failure is not null)
            {
                return Task.FromException<TResponse>(Failure);
            }

            PipeMessage response = request switch
            {
                ListAlertsRequest => new AlertListResponse(Alerts),
                _ => throw new InvalidOperationException($"Unexpected {request}"),
            };
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class FakeShell : ITrayShell
    {
        public List<string> RemovedDrives { get; } = [];

        public int RemoveAllCalls { get; private set; }

        public Task<AlertsViewModel> ShowAlertsAsync(string? alertId) => throw new InvalidOperationException("Unexpected ShowAlertsAsync");

        public void ShowSettings() => throw new InvalidOperationException("Unexpected ShowSettings");

        public void ShowAlertToast(Alert alert) => throw new InvalidOperationException("Unexpected ShowAlertToast");

        public void ShowSummaryToast(int count) => throw new InvalidOperationException("Unexpected ShowSummaryToast");

        public void ShowProcessActionToast(ProcessActionToast outcome) => throw new InvalidOperationException("Unexpected ShowProcessActionToast");

        public void RemoveAlertToasts() => RemoveAllCalls++;

        public void RemoveAlertToast(string drive) => RemovedDrives.Add(drive);

        public void OpenFolder(string folder) => throw new InvalidOperationException("Unexpected OpenFolder");

        public void ShowError(string title, string message) => throw new InvalidOperationException($"Unexpected error: {message}");

        public void Shutdown() => throw new InvalidOperationException("Unexpected Shutdown");
    }
}
