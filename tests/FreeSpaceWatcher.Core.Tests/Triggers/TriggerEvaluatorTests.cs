using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Triggers;
using Firings = System.Collections.Generic.List<(int Index, FreeSpaceWatcher.Core.Triggers.TriggerFiring Firing)>;

namespace FreeSpaceWatcher.Core.Tests.Triggers;

public sealed class TriggerEvaluatorTests
{
    private const long MiB = 1L << 20;
    private const long GiB = 1L << 30;
    private const long TotalBytes = 1024 * GiB;
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private static readonly ProcessWriteTotal[] NoWrites = [];

    [Fact]
    public void SteadyState_NeverFires()
    {
        TriggerEvaluator evaluator = Create(ResolvedThresholds.Default);

        Firings firings = Run(evaluator, 0, 600, i => 500 * GiB + (i % 2 == 0 ? MiB : -MiB));

        Assert.Empty(firings);
    }

    [Fact]
    public void LossBelowNoiseFloor_NeverFiresTimeToFull()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.TimeToFull));

        Firings firings = Run(evaluator, 0, 300, Losing(100 * MiB, 10 * MiB));

        Assert.Empty(firings);
        Assert.NotNull(evaluator.CurrentDropRate);
        Assert.Null(evaluator.CurrentTimeToFull);
    }

    [Fact]
    public void Ramp_FiresDropRate_OnceWindowSpanIsCovered()
    {
        TriggerEvaluator evaluator = Create(ResolvedThresholds.Default);

        Firings firings = Run(evaluator, 0, 100, Losing(900 * GiB, 2 * GiB));

        (int Index, TriggerFiring Firing) first = Assert.Single(firings);
        Assert.Equal(48, first.Index);
        Assert.Equal(TriggerKind.DropRate, first.Firing.Kind);
        Assert.False(first.Firing.IsEscalation);
    }

    [Fact]
    public void TimeToFull_ComputedFromSlope()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.TimeToFull) with { TimeToFullMinutes = 200 });

        Firings firings = Run(evaluator, 0, 49, i => 100 * GiB - 16 * MiB * i);

        (int Index, TriggerFiring Firing) first = Assert.Single(firings);
        Assert.Equal(48, first.Index);
        Assert.Equal(16.0 * MiB, first.Firing.DropRateBytesPerSecond, 1e-3);
        Assert.NotNull(first.Firing.TimeToFull);
        Assert.Equal(6352.0, first.Firing.TimeToFull.Value.TotalSeconds, 1e-3);
        Assert.Equal("C: losing 960.0 MB/min, full in ~106 min", first.Firing.Reason);
    }

    [Fact]
    public void Cooldown_SuppressesRepeat()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.DropRate));

        Firings firings = Run(evaluator, 0, 700, Losing(900 * GiB, 2 * GiB));

        int[] expected = [48, 648];
        Assert.Equal(expected, firings.Select(f => f.Index));
        Assert.All(firings, f => Assert.False(f.Firing.IsEscalation));
    }

    [Fact]
    public void DoubledRate_EscalatesThroughCooldown()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.DropRate));
        Func<int, long> ramp = Losing(900 * GiB, 2 * GiB);
        long at48 = ramp(48);

        Firings firings = Run(evaluator, 0, 200, i => i <= 48 ? ramp(i) : at48 - 6 * GiB * (i - 48) / 60);

        Assert.Equal(2, firings.Count);
        Assert.Equal(48, firings[0].Index);
        Assert.False(firings[0].Firing.IsEscalation);
        Assert.True(firings[1].Firing.IsEscalation);
        Assert.True(firings[1].Firing.DropRateBytesPerSecond >= 2 * firings[0].Firing.DropRateBytesPerSecond);
    }

    [Fact]
    public void HalvedTimeToFull_Escalates()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.TimeToFull));
        Func<int, long> ramp = Losing(20 * GiB, 2 * GiB);
        long at48 = ramp(48);

        Firings firings = Run(evaluator, 0, 150, i => i <= 48 ? ramp(i) : at48 - 8 * GiB * (i - 48) / 60);

        Assert.True(firings.Count >= 2);
        Assert.Equal(48, firings[0].Index);
        Assert.False(firings[0].Firing.IsEscalation);
        TriggerFiring escalation = firings[1].Firing;
        Assert.True(escalation.IsEscalation);
        Assert.True(escalation.TimeToFull <= firings[0].Firing.TimeToFull / 2);
    }

    [Fact]
    public void Floor_FiresOnce_ReArmsAboveMargin()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.Floor) with { FloorBytes = 5 * GiB, FloorPercent = 0.001 });
        double[] freeGiB = [6, 4.9, 4.8, 5.4, 4.9, 5.6, 4.9];

        Firings firings = Run(evaluator, 0, freeGiB.Length, i => (long)(freeGiB[i] * GiB));

        int[] expected = [1, 6];
        Assert.Equal(expected, firings.Select(f => f.Index));
        Assert.All(firings, f => Assert.Equal(TriggerKind.Floor, f.Firing.Kind));
    }

    [Fact]
    public void Floor_PercentBranch()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.Floor) with { FloorBytes = 1 * GiB, FloorPercent = 10 });

        Assert.Empty(evaluator.Evaluate(new DriveSample(T0, 12 * GiB, 100 * GiB), NoWrites));
        TriggerFiring firing = Assert.Single(evaluator.Evaluate(new DriveSample(T0.AddSeconds(1), 9 * GiB, 100 * GiB), NoWrites));

        Assert.Equal(TriggerKind.Floor, firing.Kind);
    }

    [Fact]
    public void ProcessWrite_FiresForProcessOverThreshold()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.ProcessWriteVolume));

        Assert.Empty(evaluator.Evaluate(Sample(0, 500 * GiB), [new ProcessWriteTotal(1, "small", 5 * GiB)]));
        TriggerFiring firing = Assert.Single(
            evaluator.Evaluate(Sample(1, 500 * GiB), [new(1, "small", 5 * GiB), new(2, "big", 11 * GiB), new(3, "bigger", 12 * GiB)])
        );

        Assert.Equal(TriggerKind.ProcessWriteVolume, firing.Kind);
        Assert.Equal(3, firing.ProcessId);
        Assert.Equal("bigger", firing.ProcessName);
        Assert.Equal("C: bigger (pid 3) wrote 12.0 GB within the write window", firing.Reason);
    }

    [Fact]
    public void ProcessWrite_DifferentProcess_Escalates()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.ProcessWriteVolume));
        ProcessWriteTotal first = new(1, "first", 11 * GiB);
        ProcessWriteTotal firstLater = first with { BytesWritten = 12 * GiB };
        ProcessWriteTotal second = new(2, "second", 13 * GiB);

        TriggerFiring initial = Assert.Single(evaluator.Evaluate(Sample(0, 500 * GiB), [first]));
        Assert.Empty(evaluator.Evaluate(Sample(1, 500 * GiB), [firstLater]));
        TriggerFiring escalation = Assert.Single(evaluator.Evaluate(Sample(2, 500 * GiB), [firstLater, second]));
        Assert.Empty(evaluator.Evaluate(Sample(3, 500 * GiB), [second with { BytesWritten = 14 * GiB }]));

        Assert.False(initial.IsEscalation);
        Assert.Equal(1, initial.ProcessId);
        Assert.True(escalation.IsEscalation);
        Assert.Equal(2, escalation.ProcessId);
    }

    [Theory]
    [InlineData(TriggerKind.DropRate)]
    [InlineData(TriggerKind.TimeToFull)]
    [InlineData(TriggerKind.Floor)]
    [InlineData(TriggerKind.ProcessWriteVolume)]
    public void DisabledTrigger_NeverFires(TriggerKind disabled)
    {
        TriggerEvaluator evaluator = Create(AllBut(disabled));
        Func<int, long> ramp = Losing(10 * GiB, 2 * GiB);
        ProcessWriteTotal[] writes = [new(7, "writer", 11 * GiB)];
        HashSet<TriggerKind> fired = [];

        for (int i = 0; i < 100; i++)
        {
            foreach (TriggerFiring firing in evaluator.Evaluate(Sample(i, ramp(i)), writes))
            {
                fired.Add(firing.Kind);
            }
        }

        Assert.DoesNotContain(disabled, fired);
        Assert.Equal(3, fired.Count);
    }

    [Fact]
    public void MarkUnavailable_ClearsWindow()
    {
        TriggerEvaluator evaluator = Create(Only(TriggerKind.DropRate));
        Func<int, long> ramp = Losing(900 * GiB, 2 * GiB);
        Assert.Empty(Run(evaluator, 0, 40, ramp));

        evaluator.MarkUnavailable();
        Assert.Null(evaluator.CurrentDropRate);
        Firings firings = Run(evaluator, 40, 100, ramp);

        (int Index, TriggerFiring Firing) first = Assert.Single(firings);
        Assert.Equal(88, first.Index);
    }

    private static TriggerEvaluator Create(ResolvedThresholds limits) => new("C", () => limits, RateWindow, Cooldown);

    private static ResolvedThresholds Only(TriggerKind kind) =>
        ResolvedThresholds.Default with
        {
            DropRateEnabled = kind == TriggerKind.DropRate,
            TimeToFullEnabled = kind == TriggerKind.TimeToFull,
            FloorEnabled = kind == TriggerKind.Floor,
            ProcessWriteEnabled = kind == TriggerKind.ProcessWriteVolume,
        };

    private static ResolvedThresholds AllBut(TriggerKind kind) =>
        ResolvedThresholds.Default with
        {
            DropRateEnabled = kind != TriggerKind.DropRate,
            TimeToFullEnabled = kind != TriggerKind.TimeToFull,
            FloorEnabled = kind != TriggerKind.Floor,
            ProcessWriteEnabled = kind != TriggerKind.ProcessWriteVolume,
        };

    private static Func<int, long> Losing(long start, long bytesPerMinute) => i => start - bytesPerMinute * i / 60;

    private static DriveSample Sample(int second, long freeBytes) => new(T0.AddSeconds(second), freeBytes, TotalBytes);

    private static Firings Run(TriggerEvaluator evaluator, int from, int to, Func<int, long> freeAt)
    {
        Firings firings = [];
        for (int i = from; i < to; i++)
        {
            foreach (TriggerFiring firing in evaluator.Evaluate(Sample(i, freeAt(i)), NoWrites))
            {
                firings.Add((i, firing));
            }
        }

        return firings;
    }
}
