using System.Diagnostics;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service.Tests.Pipe;

namespace FreeSpaceWatcher.Service.Tests.Processes;

/// <summary>Drives process actions through a real pipe, so each action runs inside the client's impersonation.</summary>
public sealed class ProcessActionsTests
{
    [Fact]
    public async Task SuspendResumeKill_ActOnTheProcess()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);
        using Process ping = StartPing();
        try
        {
            DateTimeOffset started = new(ping.StartTime);

            ProcessActionResponse suspended = await ActAsync(client, ping.Id, started, ProcessAction.Suspend, ct);
            Assert.True(suspended.Ok, suspended.Error);
            Assert.All(Threads(ping), thread => Assert.True(IsSuspended(thread), $"thread {thread.Id} is {thread.ThreadState}"));

            ProcessActionResponse resumed = await ActAsync(client, ping.Id, started, ProcessAction.Resume, ct);
            Assert.True(resumed.Ok, resumed.Error);
            Assert.DoesNotContain(Threads(ping), IsSuspended);

            ProcessActionResponse killed = await ActAsync(client, ping.Id, started, ProcessAction.Kill, ct);
            Assert.True(killed.Ok, killed.Error);
            Assert.True(ping.WaitForExit(10_000));
            Assert.Equal(1, ping.ExitCode);
        }
        finally
        {
            StopIfRunning(ping);
        }
    }

    [Fact]
    public async Task OwnProcess_IsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);

        ProcessActionResponse response = await ActAsync(client, Environment.ProcessId, null, ProcessAction.Resume, ct);

        Assert.False(response.Ok);
        Assert.False(response.AccessDenied);
        Assert.Contains("its own process", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MismatchedStartTime_IsRefused_AndTheProcessKeepsRunning()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using PipeTestHarness harness = await PipeTestHarness.StartAsync(ct);
        await using PipeTestClient client = await harness.ConnectAsync(ct);
        using Process ping = StartPing();
        try
        {
            DateTimeOffset wrongStart = new DateTimeOffset(ping.StartTime).AddHours(-1);

            ProcessActionResponse response = await ActAsync(client, ping.Id, wrongStart, ProcessAction.Kill, ct);

            Assert.False(response.Ok);
            Assert.False(response.AccessDenied);
            Assert.Contains("different process", response.Error, StringComparison.Ordinal);
            Assert.False(ping.HasExited);
        }
        finally
        {
            StopIfRunning(ping);
        }
    }

    private static Task<ProcessActionResponse> ActAsync(
        PipeTestClient client,
        int processId,
        DateTimeOffset? startTime,
        ProcessAction action,
        CancellationToken ct
    ) => client.RequestAsync<ProcessActionResponse>(new ProcessActionRequest(processId, startTime, action) { RequestId = 1 }, ct);

    private static Process StartPing()
    {
        ProcessStartInfo info = new("ping", "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        return Process.Start(info) ?? throw new InvalidOperationException("ping did not start.");
    }

    private static ProcessThread[] Threads(Process process)
    {
        process.Refresh();
        return [.. process.Threads.Cast<ProcessThread>()];
    }

    private static bool IsSuspended(ProcessThread thread) =>
        thread.ThreadState == System.Diagnostics.ThreadState.Wait && thread.WaitReason == ThreadWaitReason.Suspended;

    private static void StopIfRunning(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill();
            process.WaitForExit(10_000);
        }
    }
}
