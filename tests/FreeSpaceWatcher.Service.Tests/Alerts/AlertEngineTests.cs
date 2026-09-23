using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Alerts;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Status;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpaceWatcher.Service.Tests.Alerts;

public sealed class AlertEngineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly WriteAggregatorProvider _aggregators;
    private readonly HistoryStore _history;
    private readonly List<PipeMessage> _pushes = [];
    private readonly AlertEngine _engine;

    public AlertEngineTests()
    {
        ServicePaths paths = new(_temp.Path);
        ConfigService config = new(paths, NullLogger<ConfigService>.Instance);
        _aggregators = new WriteAggregatorProvider(config);
        _history = new HistoryStore(paths.AlertsDirectory, null);
        StatusHub hub = new();
        hub.Subscribe(_pushes.Add);
        _engine = new AlertEngine(config, _aggregators, _history, hub, NullLogger<AlertEngine>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Raise_WritesHistoryWithTheTopProcessAndCurrentSize()
    {
        string file = Path.Combine(_temp.Path, "growing.bin");
        File.WriteAllBytes(file, new byte[1234]);
        char drive = char.ToUpperInvariant(file[0]);
        WriteAggregator aggregator = _aggregators.Current;
        aggregator.ProcessStarted(4242, "writer", @"C:\tools\writer.exe", T0.AddMinutes(-10));
        aggregator.Record(
            new WriteEvent
            {
                Time = T0.AddSeconds(-10),
                ProcessId = 4242,
                Drive = drive,
                Path = file,
                Kind = WriteKind.Write,
                Bytes = 5L << 20,
            }
        );
        DriveSample[] window = [new(T0.AddSeconds(-60), 100L << 30, 500L << 30), new(T0, 99L << 30, 500L << 30)];

        Alert? alert = _engine.Raise(drive.ToString(), window[^1], window, [Firing(TriggerKind.DropRate, "losing 1 GB/min")]);

        Assert.NotNull(alert);
        Assert.True(File.Exists(Path.Combine(_temp.Path, "alerts", alert.Id + ".json")));
        Alert stored = Assert.Single(_history.List());
        ProcessWriteReport top = Assert.Single(stored.Processes);
        Assert.Equal(4242, top.ProcessId);
        Assert.Equal("writer", top.Name);
        FileWrite written = Assert.Single(top.Files);
        Assert.Equal(file, written.Path);
        Assert.Equal(1234, written.CurrentSize);
        Assert.Equal((1L << 30) - (5L << 20), stored.UnattributedBytes);
        Assert.Equal(alert.Id, Assert.IsType<AlertPush>(Assert.Single(_pushes)).Alert.Id);
    }

    [Fact]
    public void Raise_RampCrossingDropRateAndTimeToFullOnOneSample_RaisesOneAlert()
    {
        TriggerEvaluator evaluator = new("C", () => ResolvedThresholds.Default, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));
        IReadOnlyList<TriggerFiring> firings = [];
        DriveSample sample = new(T0, 0, 0);
        for (int second = 0; second <= 60 && firings.Count == 0; second++)
        {
            long free = (20L << 30) - (second * (2L << 30) / 60);
            sample = new DriveSample(T0.AddSeconds(second), free, 100L << 30);
            firings = evaluator.Evaluate(sample, []);
        }

        Alert? alert = _engine.Raise("C", sample, [sample], firings);

        Assert.Equal([TriggerKind.DropRate, TriggerKind.TimeToFull], firings.Select(f => f.Kind).Order());
        Assert.NotNull(alert);
        Assert.Equal(TriggerKind.TimeToFull, alert.Trigger);
        Assert.Equal(firings.Single(f => f.Kind == TriggerKind.TimeToFull).Reason, alert.Reason);
        Assert.Single(_history.List());
        Assert.IsType<AlertPush>(Assert.Single(_pushes));
    }

    private static TriggerFiring Firing(TriggerKind kind, string reason) =>
        new()
        {
            Kind = kind,
            IsEscalation = false,
            DropRateBytesPerSecond = (1L << 30) / 60.0,
            TimeToFull = null,
            Reason = reason,
        };
}
