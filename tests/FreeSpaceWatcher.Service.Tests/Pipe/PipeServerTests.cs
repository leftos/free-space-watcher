using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Service.Sampling;

namespace FreeSpaceWatcher.Service.Tests.Pipe;

public sealed class PipeServerTests
{
    [Fact]
    public async Task GetStatus_ReturnsTheLatestStatusWithTheRequestId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        harness.Hub.PublishStatus(StatusWith("C"));
        await using PipeTestClient client = await harness.ConnectAsync(ct);

        StatusResponse response = await client.RequestAsync<StatusResponse>(new GetStatusRequest { RequestId = 7 }, ct);

        Assert.Equal(7, response.RequestId);
        Assert.Equal("C", Assert.Single(response.Drives).Letter);
        Assert.False(response.EtwRunning);
    }

    [Fact]
    public async Task GetDriveHistory_600sRequest_Returns600sFromTheRing_CapsAt900s_AndEmptyForUnknownDrive()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        DateTimeOffset newest = new(2026, 9, 23, 14, 30, 0, TimeSpan.Zero);
        TimeSpan keep = RecentSamples.KeepFor(TimeSpan.FromSeconds(60));
        for (int age = 1000; age >= 0; age--)
        {
            harness.Samples.Add("C", new DriveSample(newest.AddSeconds(-age), 10L << 30, 100L << 30), keep);
        }

        await using PipeTestClient client = await harness.ConnectAsync(ct);

        DriveHistoryResponse tenMinutes = await client.RequestAsync<DriveHistoryResponse>(new GetDriveHistoryRequest("c", 600) { RequestId = 4 }, ct);
        DriveHistoryResponse capped = await client.RequestAsync<DriveHistoryResponse>(new GetDriveHistoryRequest("C", 5000) { RequestId = 5 }, ct);
        DriveHistoryResponse unknown = await client.RequestAsync<DriveHistoryResponse>(new GetDriveHistoryRequest("Q", 600) { RequestId = 6 }, ct);

        Assert.Equal(TimeSpan.FromSeconds(900), keep);
        Assert.Equal(4, tenMinutes.RequestId);
        Assert.Equal("c", tenMinutes.Letter);
        Assert.Equal(601, tenMinutes.Samples.Count);
        Assert.Equal(newest.AddSeconds(-600), tenMinutes.Samples[0].Time);
        Assert.Equal(newest, tenMinutes.Samples[^1].Time);
        Assert.Equal(901, capped.Samples.Count);
        Assert.Equal(newest.AddSeconds(-900), capped.Samples[0].Time);
        Assert.Equal(901, harness.Samples.Get("C", TimeSpan.FromSeconds(5000)).Count);
        Assert.Empty(unknown.Samples);
    }

    [Fact]
    public async Task UnknownMessageType_ErrorResponseEchoesTheRequestId()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);

        await client.SendLineAsync("""{"type":"getFutureThingRequest","requestId":42}""", ct);
        ErrorResponse error = await client.ReadAsync<ErrorResponse>(ct);

        Assert.Equal(42, error.RequestId);
        Assert.Contains("getFutureThingRequest", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedLine_GetsErrorResponse_AndConnectionStaysUsable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);

        await client.SendLineAsync("this is not json", ct);
        ErrorResponse error = await client.ReadAsync<ErrorResponse>(ct);
        StatusResponse after = await client.RequestAsync<StatusResponse>(new GetStatusRequest { RequestId = 2 }, ct);

        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, after.RequestId);
    }

    [Fact]
    public async Task Subscribe_ReceivesStatusPushAfterHubPublishes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);

        StatusResponse initial = await client.RequestAsync<StatusResponse>(new SubscribeRequest { RequestId = 3 }, ct);
        harness.Hub.PublishStatus(StatusWith("D"));
        StatusPush push = await client.ReadAsync<StatusPush>(ct);

        Assert.Equal(3, initial.RequestId);
        Assert.Equal(0, push.RequestId);
        Assert.Equal("D", Assert.Single(push.Status.Drives).Letter);
    }

    [Fact]
    public async Task SetConfig_InvalidConfig_ReturnsErrorsAndKeepsTheCurrentConfig()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);
        WatcherConfig invalid = harness.Config.Current with { SampleIntervalSeconds = 0 };

        SetConfigResponse response = await client.RequestAsync<SetConfigResponse>(new SetConfigRequest(invalid) { RequestId = 4 }, ct);

        Assert.Equal(4, response.RequestId);
        Assert.False(response.Ok);
        Assert.Contains(response.Errors, e => e.Contains("Sample interval", StringComparison.Ordinal));
        Assert.Equal(1, harness.Config.Current.SampleIntervalSeconds);
    }

    [Fact]
    public async Task AckAllAndDeleteAll_ChangeTheStore_AndPushAlertsChangedToOtherSubscribers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        harness.History.Save(AlertOn("C", 0));
        harness.History.Save(AlertOn("D", 1));
        await using PipeTestClient requester = await harness.ConnectAsync(ct);
        await using PipeTestClient subscriber = await harness.ConnectAsync(ct);
        await subscriber.RequestAsync<StatusResponse>(new SubscribeRequest { RequestId = 1 }, ct);

        AckResponse ack = await requester.RequestAsync<AckResponse>(new AckAlertsRequest(null) { RequestId = 2 }, ct);
        AlertsChangedPush afterAck = await subscriber.ReadAsync<AlertsChangedPush>(ct);

        Assert.Equal(2, ack.RequestId);
        Assert.Equal(2, ack.Count);
        Assert.Equal(0, afterAck.RequestId);
        Assert.All(harness.History.List(), alert => Assert.True(alert.Acknowledged));

        DeleteAlertsResponse delete = await requester.RequestAsync<DeleteAlertsResponse>(new DeleteAlertsRequest(null) { RequestId = 3 }, ct);
        AlertsChangedPush afterDelete = await subscriber.ReadAsync<AlertsChangedPush>(ct);

        Assert.Equal(3, delete.RequestId);
        Assert.Equal(2, delete.Count);
        Assert.Equal(0, afterDelete.RequestId);
        Assert.Empty(harness.History.List());
    }

    private static Alert AlertOn(string drive, int minute)
    {
        DateTimeOffset time = new(2026, 9, 23, 14, minute, 0, TimeSpan.Zero);
        return new Alert
        {
            Id = Alert.CreateId(time, drive, TriggerKind.Floor),
            Time = time,
            Drive = drive,
            Trigger = TriggerKind.Floor,
            IsEscalation = false,
            Reason = $"{drive}: below the floor",
            FreeBytes = 1L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 0,
            TimeToFull = null,
            UnattributedBytes = 0,
            Processes = [],
        };
    }

    private static StatusResponse StatusWith(string letter) =>
        new(
            [
                new DriveStatus
                {
                    Letter = letter,
                    Watched = true,
                    Available = true,
                    FreeBytes = 10L << 30,
                    TotalBytes = 100L << 30,
                    DropRateBytesPerSecond = null,
                    TimeToFull = null,
                },
            ],
            false,
            null
        );
}
