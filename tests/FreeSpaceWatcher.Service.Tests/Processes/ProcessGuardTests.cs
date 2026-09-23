using System.Diagnostics;
using FreeSpaceWatcher.Native;

namespace FreeSpaceWatcher.Service.Tests.Processes;

public sealed class ProcessGuardTests
{
    [Fact]
    public void Check_ProtectedProcess_IsRefused()
    {
        ProcessControlResult result = ProcessGuard.Check(Environment.ProcessId, null, Environment.ProcessId);

        Assert.False(result.Ok);
        Assert.False(result.AccessDenied);
        Assert.Equal("FreeSpaceWatcher does not act on its own process.", result.Error);
    }

    [Fact]
    public void Check_MismatchedStartTime_IsRefused()
    {
        using Process ping = StartPing();
        try
        {
            DateTimeOffset wrongStart = new DateTimeOffset(ping.StartTime).AddHours(-1);

            ProcessControlResult result = ProcessGuard.Check(ping.Id, wrongStart, Environment.ProcessId);

            Assert.False(result.Ok);
            Assert.False(result.AccessDenied);
            Assert.Contains("different process", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            ping.Kill();
            ping.WaitForExit(10_000);
        }
    }

    [Fact]
    public void Check_ExitedProcess_SaysItIsNotRunning()
    {
        int pid;
        using (Process ping = StartPing())
        {
            pid = ping.Id;
            ping.Kill();
            Assert.True(ping.WaitForExit(10_000));
        }

        // Windows keeps an exited process's id valid until the last handle to it closes, which other processes may hold briefly.
        ProcessControlResult result = ProcessGuard.Check(pid, null, Environment.ProcessId);
        var waited = Stopwatch.StartNew();
        while (result.Ok && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(100);
            result = ProcessGuard.Check(pid, null, Environment.ProcessId);
        }

        Assert.False(result.Ok);
        Assert.False(result.AccessDenied);
        Assert.Equal($"No process with id {pid} is running.", result.Error);
    }

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
}
