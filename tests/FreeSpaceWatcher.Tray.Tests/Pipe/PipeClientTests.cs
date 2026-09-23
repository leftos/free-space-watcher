using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Tests.Pipe;

public sealed class PipeClientTests
{
    private static readonly TimeSpan NormalTimeout = TimeSpan.FromSeconds(10);
    private readonly FakeTrayLog _log = new();

    [Fact]
    public async Task SendAsync_ResponsesOutOfOrderBetweenPushes_EachRequestGetsItsOwnResponse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakePipeServer server = new();
        await using PipeClient client = new(server.Name, NormalTimeout, _log);
        ClientEvents events = new(client);
        client.Start();
        await using FakePipeSession session = await server.AcceptAsync(ct);
        await session.AnswerSubscribeAsync(ct);
        Assert.True(await events.NextConnectionAsync(ct));
        Assert.Empty((await events.NextStatusAsync(ct)).Drives);

        Task<StatusResponse> status = client.SendAsync<StatusResponse>(new GetStatusRequest(), ct);
        Task<AlertListResponse> alerts = client.SendAsync<AlertListResponse>(new ListAlertsRequest(), ct);
        PipeMessage first = await session.ReceiveAsync(ct);
        PipeMessage second = await session.ReceiveAsync(ct);
        PipeMessage statusRequest = first is GetStatusRequest ? first : second;
        PipeMessage alertsRequest = first is ListAlertsRequest ? first : second;
        Assert.NotEqual(statusRequest.RequestId, alertsRequest.RequestId);

        await session.SendAsync(new StatusPush(FakePipeSession.Status("D")), ct);
        await session.SendAsync(new AlertListResponse([]) { RequestId = alertsRequest.RequestId }, ct);
        await session.SendAsync(new AlertPush(NewAlert()), ct);
        await session.SendAsync(FakePipeSession.Status("C") with { RequestId = statusRequest.RequestId }, ct);

        Assert.Equal("C", Assert.Single((await status).Drives).Letter);
        Assert.Empty((await alerts).Alerts);
        Assert.Equal("D", Assert.Single((await events.NextStatusAsync(ct)).Drives).Letter);
        Assert.Equal(NewAlert().Id, (await events.NextAlertAsync(ct)).Id);
    }

    [Fact]
    public async Task SendAsync_ServiceNeverAnswers_ThrowsTimeoutException()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakePipeServer server = new();
        await using PipeClient client = new(server.Name, TimeSpan.FromMilliseconds(300), _log);
        ClientEvents events = new(client);
        client.Start();
        await using FakePipeSession session = await server.AcceptAsync(ct);
        await session.AnswerSubscribeAsync(ct);
        Assert.True(await events.NextConnectionAsync(ct));

        Task<AlertListResponse> pending = client.SendAsync<AlertListResponse>(new ListAlertsRequest(), ct);
        Assert.IsType<ListAlertsRequest>(await session.ReceiveAsync(ct));

        await Assert.ThrowsAsync<TimeoutException>(() => pending);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task ServerDropsConnection_ClientReportsFalseThenReconnectsAndReportsTrue()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        FakePipeServer server = new();
        await using PipeClient client = new(server.Name, NormalTimeout, _log);
        ClientEvents events = new(client);
        client.Start();
        await using (FakePipeSession first = await server.AcceptAsync(ct))
        {
            await first.AnswerSubscribeAsync(ct);
            Assert.True(await events.NextConnectionAsync(ct));
        }

        Assert.False(await events.NextConnectionAsync(ct));
        Assert.False(client.IsConnected);

        await using FakePipeSession second = await server.AcceptAsync(ct);
        await second.AnswerSubscribeAsync(ct);
        Assert.True(await events.NextConnectionAsync(ct));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task SendAsync_NotConnected_ThrowsIOException()
    {
        await using PipeClient client = new("FreeSpaceWatcher.Tests." + Guid.NewGuid().ToString("N"), NormalTimeout, _log);

        await Assert.ThrowsAsync<IOException>(() => client.SendAsync<StatusResponse>(new GetStatusRequest(), TestContext.Current.CancellationToken));
        Assert.Empty(_log.Lines);
    }

    private static Alert NewAlert() =>
        new()
        {
            Id = "20260923-141500-C-DropRate",
            Time = new DateTimeOffset(2026, 9, 23, 14, 15, 0, TimeSpan.Zero),
            Drive = "C",
            Trigger = TriggerKind.DropRate,
            IsEscalation = false,
            Reason = "C: losing 2.1 GB/min, full in ~9 min",
            FreeBytes = 1,
            TotalBytes = 2,
            DropRateBytesPerSecond = 1,
            TimeToFull = null,
            UnattributedBytes = 0,
            Processes = [],
        };
}
