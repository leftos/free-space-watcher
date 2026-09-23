using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;

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
