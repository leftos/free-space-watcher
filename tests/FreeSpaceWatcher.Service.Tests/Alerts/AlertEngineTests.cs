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
    private const int WriterPid = 4242;
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly ConfigService _config;
    private readonly WriteAggregatorProvider _aggregators;
    private readonly HistoryStore _history;
    private readonly List<PipeMessage> _pushes = [];
    private readonly AlertEngine _engine;

    public AlertEngineTests()
    {
        ServicePaths paths = new(_temp.Path);
        _config = new ConfigService(paths, NullLogger<ConfigService>.Instance);
        _aggregators = new WriteAggregatorProvider(_config);
        _history = new HistoryStore(paths.AlertsDirectory, null);
        StatusHub hub = new();
        hub.Subscribe(_pushes.Add);
        _engine = new AlertEngine(_config, _aggregators, _history, hub, new FileSizeProbe(), NullLogger<AlertEngine>.Instance);
        _aggregators.Current.ProcessStarted(WriterPid, "writer", @"C:\tools\writer.exe", T0.AddMinutes(-10));
    }

    private string Letter => char.ToUpperInvariant(_temp.Path[0]).ToString();

    private string AlertsDirectory => Path.Combine(_temp.Path, "alerts");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Raise_WritesHistoryWithTheTopProcessAndCurrentSize()
    {
        string file = Path.Combine(_temp.Path, "growing.bin");
        File.WriteAllBytes(file, new byte[1234]);
        _aggregators.Current.Record(Event(file, WriteKind.Write, 5L << 20));
        DriveSample[] window = [new(T0.AddSeconds(-60), 100L << 30, 500L << 30), new(T0, 99L << 30, 500L << 30)];

        Alert? alert = _engine.Raise(Letter, window[^1], window, [Firing(TriggerKind.DropRate)], AnyEvaluator());

        Assert.NotNull(alert);
        Assert.True(File.Exists(Path.Combine(AlertsDirectory, alert.Id + ".json")));
        Alert stored = Assert.Single(_history.List());
        ProcessWriteReport top = Assert.Single(stored.Processes);
        Assert.Equal(WriterPid, top.ProcessId);
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

        Alert? alert = _engine.Raise("C", sample, [sample], firings, evaluator);

        Assert.Equal([TriggerKind.DropRate, TriggerKind.TimeToFull], firings.Select(f => f.Kind).Order());
        Assert.NotNull(alert);
        Assert.Equal(TriggerKind.TimeToFull, alert.Trigger);
        Assert.Equal(firings.Single(f => f.Kind == TriggerKind.TimeToFull).Reason, alert.Reason);
        Assert.Single(_history.List());
        Assert.IsType<AlertPush>(Assert.Single(_pushes));
    }

    [Fact]
    public void Grace_WritersDeleteTheirFiles_AlertDiscarded()
    {
        Configure(graceSeconds: 20, resolveMinutes: 5);
        string file = GrowFile("ramp.bin", 1 << 20);
        TriggerEvaluator evaluator = DropRateOnly();
        (int second, DriveSample sample, IReadOnlyList<TriggerFiring> firings) = RampUntilFiring(evaluator, 0);

        Assert.Null(_engine.Raise(Letter, sample, [sample], firings, evaluator));
        File.Delete(file);
        _engine.Tick(sample.Time.AddSeconds(1));
        _engine.Tick(sample.Time.AddSeconds(30));

        Assert.False(Directory.Exists(AlertsDirectory) && Directory.EnumerateFileSystemEntries(AlertsDirectory).Any());
        Assert.Empty(_history.List());
        Assert.Empty(_pushes);
        (int nextSecond, _, IReadOnlyList<TriggerFiring> next) = RampUntilFiring(evaluator, second + 1);
        Assert.Equal(second + 1, nextSecond);
        Assert.False(Assert.Single(next).IsEscalation);
    }

    [Fact]
    public void Grace_Elapses_AlertRaised()
    {
        Configure(graceSeconds: 20, resolveMinutes: 5);
        GrowFile("kept.bin", 1 << 20);

        Assert.Null(_engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator()));
        _engine.Tick(T0.AddSeconds(19));
        Assert.Empty(_history.List());
        Assert.Empty(_pushes);
        _engine.Tick(T0.AddSeconds(20));

        Alert stored = Assert.Single(_history.List());
        Assert.Equal(TriggerKind.DropRate, stored.Trigger);
        Assert.Equal(T0, stored.Time);
        Assert.Equal(stored.Id, Assert.IsType<AlertPush>(Assert.Single(_pushes)).Alert.Id);
    }

    [Fact]
    public void Grace_EscalationWhileHeld_RaisedImmediately()
    {
        Configure(graceSeconds: 20, resolveMinutes: 5);
        GrowFile("kept.bin", 1 << 20);
        TriggerEvaluator evaluator = AnyEvaluator();
        Assert.Null(_engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], evaluator));

        Alert? escalation = _engine.Raise(
            Letter,
            Sample(T0.AddSeconds(5)),
            [],
            [Firing(TriggerKind.DropRate, isEscalation: true, reason: "losing 4 GB/min")],
            evaluator
        );
        _engine.Tick(T0.AddSeconds(30));

        Assert.NotNull(escalation);
        Assert.True(escalation.IsEscalation);
        Assert.Equal("losing 4 GB/min", escalation.Reason);
        Assert.Equal(T0.AddSeconds(5), escalation.Time);
        Assert.Equal(escalation.Id, Assert.Single(_history.List()).Id);
        Assert.IsType<AlertPush>(Assert.Single(_pushes));
    }

    [Fact]
    public void Grace_FloorAndWriteVolume_NotHeld()
    {
        Configure(graceSeconds: 20, resolveMinutes: 5);
        GrowFile("kept.bin", 1 << 20);

        Alert? floor = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.Floor)], AnyEvaluator());
        Alert? volume = _engine.Raise(Letter, Sample(T0.AddSeconds(1)), [], [Firing(TriggerKind.ProcessWriteVolume)], AnyEvaluator());

        Assert.NotNull(floor);
        Assert.NotNull(volume);
        Assert.Equal(2, _history.List().Count);
        Assert.Equal(2, _pushes.OfType<AlertPush>().Count());
    }

    [Fact]
    public void Grace_NoTrackedGrowth_NotHeld()
    {
        Configure(graceSeconds: 20, resolveMinutes: 5);
        string file = Path.Combine(_temp.Path, "overwritten.bin");
        File.WriteAllBytes(file, new byte[1 << 20]);
        _aggregators.Current.Record(Event(file, WriteKind.Write, 1 << 20));

        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.TimeToFull)], AnyEvaluator());

        Assert.NotNull(alert);
        Assert.Single(_history.List());
    }

    [Fact]
    public void Grace_Zero_Disables()
    {
        Configure(graceSeconds: 0, resolveMinutes: 5);
        GrowFile("kept.bin", 1 << 20);

        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator());

        Assert.NotNull(alert);
        Assert.Single(_history.List());
    }

    [Fact]
    public void Resolve_WritersDeleteFiles_MarksResolvedAcknowledgedAndPushes()
    {
        Configure(graceSeconds: 0, resolveMinutes: 5);
        string file = GrowFile("ramp.bin", 1 << 20);
        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator());
        Assert.NotNull(alert);

        File.Delete(file);
        _engine.Tick(T0.AddSeconds(30));

        Alert? stored = _history.Get(alert.Id);
        Assert.NotNull(stored);
        Assert.True(stored.Acknowledged);
        Assert.Equal(T0.AddSeconds(30), stored.ResolvedAt);
        Assert.Equal("writer deleted 1.0 MB it had written", stored.ResolvedReason);
        Assert.Collection(
            _pushes,
            push => Assert.IsType<AlertPush>(push),
            push => Assert.IsType<AlertsChangedPush>(push),
            push => Assert.Equal(stored.ResolvedReason, Assert.IsType<AlertResolvedPush>(push).Alert.ResolvedReason)
        );
        _engine.Tick(T0.AddSeconds(31));
        Assert.Equal(3, _pushes.Count);
    }

    [Fact]
    public void Resolve_FilesShrinkBackPartly_StaysOpenAbove10Percent()
    {
        Configure(graceSeconds: 0, resolveMinutes: 5);
        string file = GrowFile("shrinking.bin", 1_000_000);
        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator());
        Assert.NotNull(alert);

        SetLength(file, 200_000);
        _engine.Tick(T0.AddSeconds(10));
        Alert? open = _history.Get(alert.Id);
        SetLength(file, 100_000);
        _engine.Tick(T0.AddSeconds(20));

        Assert.NotNull(open);
        Assert.Null(open.ResolvedAt);
        Assert.False(open.Acknowledged);
        Assert.Equal(T0.AddSeconds(20), _history.Get(alert.Id)?.ResolvedAt);
    }

    [Fact]
    public void Resolve_WindowEnds_StopsWatching()
    {
        Configure(graceSeconds: 0, resolveMinutes: 5);
        string file = GrowFile("late.bin", 1 << 20);
        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator());
        Assert.NotNull(alert);

        _engine.Tick(T0.AddMinutes(1));
        File.Delete(file);
        _engine.Tick(T0.AddMinutes(5).AddSeconds(1));
        _engine.Tick(T0.AddMinutes(6));

        Alert? stored = _history.Get(alert.Id);
        Assert.NotNull(stored);
        Assert.Null(stored.ResolvedAt);
        Assert.False(stored.Acknowledged);
        Assert.IsType<AlertPush>(Assert.Single(_pushes));
    }

    [Fact]
    public void Resolve_Zero_Disables()
    {
        Configure(graceSeconds: 0, resolveMinutes: 0);
        string file = GrowFile("ramp.bin", 1 << 20);
        Alert? alert = _engine.Raise(Letter, Sample(T0), [], [Firing(TriggerKind.DropRate)], AnyEvaluator());
        Assert.NotNull(alert);

        File.Delete(file);
        _engine.Tick(T0.AddSeconds(30));

        Assert.Null(_history.Get(alert.Id)?.ResolvedAt);
        Assert.IsType<AlertPush>(Assert.Single(_pushes));
    }

    private static TriggerFiring Firing(TriggerKind kind, bool isEscalation = false, string reason = "losing 1 GB/min") =>
        new()
        {
            Kind = kind,
            IsEscalation = isEscalation,
            DropRateBytesPerSecond = (1L << 30) / 60.0,
            TimeToFull = null,
            Reason = reason,
        };

    private static DriveSample Sample(DateTimeOffset time) => new(time, 100L << 30, 500L << 30);

    private static TriggerEvaluator AnyEvaluator() => new("C", () => ResolvedThresholds.Default, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));

    private static void SetLength(string file, long length)
    {
        using FileStream stream = new(file, FileMode.Open, FileAccess.Write);
        stream.SetLength(length);
    }

    private static WriteEvent Event(string path, WriteKind kind, long bytes) =>
        new()
        {
            Time = T0.AddSeconds(-10),
            ProcessId = WriterPid,
            Drive = char.ToUpperInvariant(path[0]),
            Path = path,
            Kind = kind,
            Bytes = bytes,
        };

    private TriggerEvaluator DropRateOnly()
    {
        ResolvedThresholds limits = ResolvedThresholds.Default with { TimeToFullEnabled = false, FloorEnabled = false, ProcessWriteEnabled = false };
        return new TriggerEvaluator(Letter, () => limits, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));
    }

    private void Configure(int graceSeconds, int resolveMinutes)
    {
        WatcherConfig current = _config.Current;
        ResolvedThresholds defaults = current.Defaults with { GraceSeconds = graceSeconds, ResolveMinutes = resolveMinutes };
        Assert.Empty(_config.Update(current with { Defaults = defaults }));
    }

    private string GrowFile(string name, int bytes)
    {
        string file = Path.Combine(_temp.Path, name);
        File.WriteAllBytes(file, new byte[bytes]);
        _aggregators.Current.Record(Event(file, WriteKind.Write, bytes));
        _aggregators.Current.Record(Event(file, WriteKind.Extend, bytes));
        return file;
    }

    private static (int Second, DriveSample Sample, IReadOnlyList<TriggerFiring> Firings) RampUntilFiring(TriggerEvaluator evaluator, int from)
    {
        for (int second = from; second < from + 120; second++)
        {
            DriveSample sample = new(T0.AddSeconds(second), (900L << 30) - (second * (2L << 30) / 60), 1024L << 30);
            IReadOnlyList<TriggerFiring> firings = evaluator.Evaluate(sample, []);
            if (firings.Count > 0)
            {
                return (second, sample, firings);
            }
        }

        throw new InvalidOperationException($"The ramp did not fire between seconds {from} and {from + 120}.");
    }
}
