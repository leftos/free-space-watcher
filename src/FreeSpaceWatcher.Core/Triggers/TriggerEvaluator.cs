using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using static System.FormattableString;

namespace FreeSpaceWatcher.Core.Triggers;

/// <summary>Decides, sample by sample, which of one drive's triggers fire.</summary>
/// <remarks>
/// The drop rate is the least-squares slope of free space over the samples inside the rate window, and is only known once
/// those samples span at least 80 % of the window. Each trigger except the floor has a cooldown that an escalation bypasses.
/// The drop-rate and time-to-full triggers share one cooldown clock, so when either fires the other's cooldown starts too from the
/// next sample on (both may fire on the same sample, which the alert engine merges into one alert), and an
/// escalation of either needs at least <see cref="MinEscalationGap"/> since that trigger's own last firing: while a ramp fills the
/// rate window the fitted slope keeps climbing, and without the gap one ramp would raise an alert every few seconds. The
/// process-write trigger keeps its own cooldown and escalates at once. The floor fires once and re-arms when free space climbs
/// back above the floor by 10 %.
/// </remarks>
/// <param name="driveLetter">The drive letter, used in the reason sentences.</param>
/// <param name="thresholds">Returns the drive's current thresholds; called on every sample so configuration changes apply at once.</param>
/// <param name="rateWindow">The window the drop rate is fitted over.</param>
/// <param name="cooldown">The minimum time between two firings of the same trigger (or of the two rate triggers), escalations aside.</param>
public sealed class TriggerEvaluator(string driveLetter, Func<ResolvedThresholds> thresholds, TimeSpan rateWindow, TimeSpan cooldown)
{
    /// <summary>The least time between a drop-rate or time-to-full firing and an escalation of the same trigger.</summary>
    public static readonly TimeSpan MinEscalationGap = TimeSpan.FromSeconds(60);

    private const double FloorReArmFactor = 1.10;
    private readonly string _label = WatcherConfig.NormalizeLetter(driveLetter) + ":";
    private readonly List<DriveSample> _samples = [];
    private readonly Dictionary<TriggerKind, LastFiring> _lastFirings = [];
    private readonly Dictionary<TriggerKind, Retraction> _retractions = [];
    private DateTimeOffset? _rateTriggersLastFired;
    private DateTimeOffset? _rateTriggersFiredBefore;
    private bool _floorArmed = true;

    private enum FiringMode
    {
        Suppressed,
        Fire,
        Escalate,
    }

    /// <summary>Gets the current drop rate in bytes per second (positive = losing space), or null until the window is covered.</summary>
    public double? CurrentDropRate { get; private set; }

    /// <summary>Gets the current time-to-full estimate, or null when the drive is not losing space faster than the noise floor.</summary>
    public TimeSpan? CurrentTimeToFull { get; private set; }

    /// <summary>Adds a sample and returns the triggers that fire on it.</summary>
    /// <param name="sample">The newest free-space reading.</param>
    /// <param name="writes">Each process's net growth on this drive within the write window.</param>
    /// <returns>The firings, empty when nothing fires.</returns>
    public IReadOnlyList<TriggerFiring> Evaluate(DriveSample sample, IReadOnlyList<ProcessWriteTotal> writes)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(writes);
        ResolvedThresholds limits = thresholds();
        AddSample(sample);
        UpdateRates(sample.FreeBytes, limits);

        List<TriggerFiring> firings = [];
        EvaluateDropRate(sample.Time, limits, firings);
        EvaluateTimeToFull(sample.Time, limits, firings);
        if (firings.Count > 0)
        {
            _rateTriggersFiredBefore = _rateTriggersLastFired;
            _rateTriggersLastFired = sample.Time;
        }

        EvaluateFloor(sample, limits, firings);
        EvaluateProcessWrite(sample.Time, limits, writes, firings);
        return firings;
    }

    /// <summary>Undoes a trigger's most recent firing, so a discarded alert starts no cooldown.</summary>
    /// <remarks>
    /// The trigger's cooldown state goes back to what it was before that firing, and so does the shared drop-rate and time-to-full
    /// clock when that firing started it; a retracted floor firing re-arms the floor. One level of history is kept per trigger, so
    /// a second retraction without a firing in between, or a retraction of a trigger that never fired, does nothing.
    /// </remarks>
    /// <param name="kind">The trigger whose most recent firing is undone.</param>
    public void Retract(TriggerKind kind)
    {
        if (!_retractions.Remove(kind, out Retraction? retraction))
        {
            return;
        }

        if (kind == TriggerKind.Floor)
        {
            _floorArmed = true;
            return;
        }

        if (retraction.Previous is LastFiring previous)
        {
            _lastFirings[kind] = previous;
        }
        else
        {
            _lastFirings.Remove(kind);
        }

        if ((kind is TriggerKind.DropRate or TriggerKind.TimeToFull) && _rateTriggersLastFired == retraction.Time)
        {
            _rateTriggersLastFired = _rateTriggersFiredBefore;
        }
    }

    /// <summary>Forgets the samples because the drive went away; the rate must be re-learned from new samples.</summary>
    public void MarkUnavailable()
    {
        _samples.Clear();
        CurrentDropRate = null;
        CurrentTimeToFull = null;
    }

    private void AddSample(DriveSample sample)
    {
        _samples.Add(sample);
        DateTimeOffset oldest = sample.Time - rateWindow;
        _samples.RemoveAll(s => s.Time < oldest);
    }

    private void UpdateRates(long freeBytes, ResolvedThresholds limits)
    {
        CurrentDropRate = WindowIsCovered() ? LeastSquaresLossRate() : null;
        CurrentTimeToFull =
            CurrentDropRate is double rate && rate > 0 && rate * 60 >= limits.NoiseFloorBytesPerMinute ? TimeToFull(freeBytes, rate) : null;
    }

    private bool WindowIsCovered() => _samples.Count >= 2 && (_samples[^1].Time - _samples[0].Time).Ticks >= rateWindow.Ticks * 4 / 5;

    private double? LeastSquaresLossRate()
    {
        DateTimeOffset origin = _samples[0].Time;
        double meanX = 0;
        double meanY = 0;
        foreach (DriveSample sample in _samples)
        {
            meanX += (sample.Time - origin).TotalSeconds;
            meanY += sample.FreeBytes;
        }

        meanX /= _samples.Count;
        meanY /= _samples.Count;
        double covariance = 0;
        double variance = 0;
        foreach (DriveSample sample in _samples)
        {
            double dx = (sample.Time - origin).TotalSeconds - meanX;
            covariance += dx * (sample.FreeBytes - meanY);
            variance += dx * dx;
        }

        return variance > 0 ? -(covariance / variance) : null;
    }

    private static TimeSpan TimeToFull(long freeBytes, double lossRate)
    {
        double seconds = Math.Max(0, freeBytes) / lossRate;
        return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
    }

    private void EvaluateDropRate(DateTimeOffset now, ResolvedThresholds limits, List<TriggerFiring> firings)
    {
        if (!limits.DropRateEnabled || CurrentDropRate is not double rate || rate * 60 < limits.DropRateBytesPerMinute)
        {
            return;
        }

        FiringMode mode = RateGate(TriggerKind.DropRate, now, last => rate >= 2 * last.DropRate);
        if (mode != FiringMode.Suppressed)
        {
            firings.Add(Fire(TriggerKind.DropRate, now, mode, RateReason(rate, CurrentTimeToFull), null));
        }
    }

    private void EvaluateTimeToFull(DateTimeOffset now, ResolvedThresholds limits, List<TriggerFiring> firings)
    {
        if (
            !limits.TimeToFullEnabled
            || CurrentDropRate is not double rate
            || CurrentTimeToFull is not TimeSpan timeToFull
            || timeToFull.TotalMinutes >= limits.TimeToFullMinutes
        )
        {
            return;
        }

        FiringMode mode = RateGate(TriggerKind.TimeToFull, now, last => last.TimeToFull is TimeSpan previous && timeToFull <= previous / 2);
        if (mode != FiringMode.Suppressed)
        {
            firings.Add(Fire(TriggerKind.TimeToFull, now, mode, RateReason(rate, timeToFull), null));
        }
    }

    private void EvaluateFloor(DriveSample sample, ResolvedThresholds limits, List<TriggerFiring> firings)
    {
        if (!limits.FloorEnabled)
        {
            return;
        }

        double percentFloor = limits.FloorPercent / 100 * Math.Max(0, sample.TotalBytes);
        if (!_floorArmed)
        {
            _floorArmed = sample.FreeBytes > limits.FloorBytes * FloorReArmFactor && sample.FreeBytes > percentFloor * FloorReArmFactor;
            return;
        }

        if (sample.FreeBytes < limits.FloorBytes || sample.FreeBytes < percentFloor)
        {
            _floorArmed = false;
            _retractions[TriggerKind.Floor] = new Retraction(sample.Time, null);
            string reason = Invariant(
                $"{_label} only {ByteFormat.Format(sample.FreeBytes)} free, below the floor of {ByteFormat.Format(limits.FloorBytes)} or {limits.FloorPercent:0.##} %"
            );
            firings.Add(Firing(TriggerKind.Floor, false, reason, null));
        }
    }

    private void EvaluateProcessWrite(
        DateTimeOffset now,
        ResolvedThresholds limits,
        IReadOnlyList<ProcessWriteTotal> writes,
        List<TriggerFiring> firings
    )
    {
        if (!limits.ProcessWriteEnabled)
        {
            return;
        }

        ProcessWriteTotal? top = writes
            .Where(w => w.NetGrowthBytes >= limits.ProcessWriteBytes)
            .OrderByDescending(w => w.NetGrowthBytes)
            .ThenBy(w => w.ProcessId)
            .FirstOrDefault();
        if (top is null)
        {
            return;
        }

        FiringMode mode = Gate(TriggerKind.ProcessWriteVolume, now, last => last.ProcessId != top.ProcessId);
        if (mode != FiringMode.Suppressed)
        {
            string reason = Invariant(
                $"{_label} {top.ProcessName} (pid {top.ProcessId}) grew its files by {ByteFormat.Format(top.NetGrowthBytes)} within the write window"
            );
            firings.Add(Fire(TriggerKind.ProcessWriteVolume, now, mode, reason, top));
        }
    }

    private FiringMode Gate(TriggerKind kind, DateTimeOffset now, Func<LastFiring, bool> escalates)
    {
        if (!_lastFirings.TryGetValue(kind, out LastFiring? last) || now - last.Time >= cooldown)
        {
            return FiringMode.Fire;
        }

        return escalates(last) ? FiringMode.Escalate : FiringMode.Suppressed;
    }

    private FiringMode RateGate(TriggerKind kind, DateTimeOffset now, Func<LastFiring, bool> escalates)
    {
        if (_rateTriggersLastFired is not DateTimeOffset lastRateFiring || now - lastRateFiring >= cooldown)
        {
            return FiringMode.Fire;
        }

        return _lastFirings.TryGetValue(kind, out LastFiring? last) && now - last.Time >= MinEscalationGap && escalates(last)
            ? FiringMode.Escalate
            : FiringMode.Suppressed;
    }

    private TriggerFiring Fire(TriggerKind kind, DateTimeOffset now, FiringMode mode, string reason, ProcessWriteTotal? process)
    {
        _retractions[kind] = new Retraction(now, _lastFirings.GetValueOrDefault(kind));
        _lastFirings[kind] = new LastFiring(now, CurrentDropRate ?? 0, CurrentTimeToFull, process?.ProcessId);
        return Firing(kind, mode == FiringMode.Escalate, reason, process);
    }

    private TriggerFiring Firing(TriggerKind kind, bool isEscalation, string reason, ProcessWriteTotal? process) =>
        new()
        {
            Kind = kind,
            IsEscalation = isEscalation,
            DropRateBytesPerSecond = CurrentDropRate ?? 0,
            TimeToFull = CurrentTimeToFull,
            Reason = reason,
            ProcessId = process?.ProcessId,
            ProcessName = process?.ProcessName,
        };

    private string RateReason(double lossRate, TimeSpan? timeToFull)
    {
        string losing = $"{_label} losing {ByteFormat.Format((long)(lossRate * 60))}/min";
        return timeToFull is TimeSpan eta ? $"{losing}, full in {FormatDuration(eta)}" : losing;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
        {
            return "under a minute";
        }

        return duration.TotalMinutes < 120
            ? Invariant($"~{Math.Round(duration.TotalMinutes):0} min")
            : Invariant($"~{Math.Round(duration.TotalHours):0} h");
    }

    private sealed record LastFiring(DateTimeOffset Time, double DropRate, TimeSpan? TimeToFull, int? ProcessId);

    /// <summary>What a retraction of a trigger's most recent firing restores.</summary>
    /// <param name="Time">When that firing happened.</param>
    /// <param name="Previous">The trigger's last firing before it, or null when there was none.</param>
    private sealed record Retraction(DateTimeOffset Time, LastFiring? Previous);
}
