using System.Diagnostics;
using System.Security.Principal;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Etw;
using FreeSpaceWatcher.Service.Native;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpaceWatcher.Service.Tests.Etw;

/// <summary>Runs a real kernel ETW session; skipped unless the test process is elevated.</summary>
public sealed class WriteCollectorIntegrationTests
{
    private const int ChunkBytes = 1 << 20;
    private const int ChunkCount = 200;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Collector_AttributesTempFileWritesToThisProcessAndPath()
    {
        if (!IsElevated())
        {
            Assert.Skip("ETW kernel tracing needs an elevated test run.");
        }

        CancellationToken ct = TestContext.Current.CancellationToken;
        using TempDirectory data = new();
        string folder = Path.Combine(AppContext.BaseDirectory, $"etw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        ConfigService config = new(new ServicePaths(data.Path), NullLogger<ConfigService>.Instance);
        WriteAggregatorProvider aggregators = new(config);
        DevicePathMapper mapper = new(DosDevices.Query, TimeProvider.System);
        // The collector runs in the test process, so it must not ignore this pid the way the service ignores its own.
        WriteCollectorOptions options = new($"FreeSpaceWatcher.Tests.{Guid.NewGuid():N}", IgnoredProcessId: 0);
        using WriteCollector collector = new(aggregators, mapper, NullLogger<WriteCollector>.Instance, options);
        await collector.StartAsync(ct);
        try
        {
            await WaitUntilAsync(() => collector.Running, ct);
            Assert.True(collector.Running, collector.ErrorMessage);
            string file = Path.Combine(folder, "written.bin");
            await WriteFileAsync(file, ct);

            FileWrite? written = null;
            await WaitUntilAsync(() => (written = FindOwnWrite(aggregators, file)) is { BytesWritten: >= (long)ChunkBytes * ChunkCount }, ct);

            Assert.NotNull(written);
            Assert.True(written.BytesWritten >= (long)ChunkBytes * ChunkCount, $"{written.BytesWritten} bytes attributed to {file}");
            IEnumerable<ProcessWriteReport> systemWriters = Snapshot(aggregators, file)
                .Processes.Where(p => string.Equals(p.Name, "System", StringComparison.OrdinalIgnoreCase))
                .Where(p => p.Files.Any(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase)));
            Assert.Empty(systemWriters);
        }
        finally
        {
            await collector.StopAsync(CancellationToken.None);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task WriteFileAsync(string path, CancellationToken ct)
    {
        byte[] chunk = new byte[ChunkBytes];
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 0);
        for (int i = 0; i < ChunkCount; i++)
        {
            await stream.WriteAsync(chunk, ct);
        }

        stream.Flush(flushToDisk: true);
    }

    private static DriveWriteSnapshot Snapshot(WriteAggregatorProvider aggregators, string file) =>
        aggregators.Current.Snapshot(file[0], DateTimeOffset.Now, topProcesses: 1000, topFolders: 10, topFiles: 1000);

    private static FileWrite? FindOwnWrite(WriteAggregatorProvider aggregators, string file) =>
        Snapshot(aggregators, file)
            .Processes.Where(p => p.ProcessId == Environment.ProcessId)
            .SelectMany(p => p.Files)
            .FirstOrDefault(f => string.Equals(f.Path, file, StringComparison.OrdinalIgnoreCase));

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < Deadline)
        {
            await Task.Delay(100, ct);
        }
    }
}
