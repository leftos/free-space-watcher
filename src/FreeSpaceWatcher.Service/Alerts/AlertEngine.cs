using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Status;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.Logging;
using static System.FormattableString;

namespace FreeSpaceWatcher.Service.Alerts;

/// <summary>Turns the firings of one drive sample into one alert: stored in history, logged, and pushed to subscribers.</summary>
/// <remarks>
/// <para>
/// When an alert is built, the growth of its top writers' listed files is tracked: each file's size before the growth and the
/// growth itself (<see cref="TrackedGrowth"/>). A drop-rate or time-to-full alert with tracked growth is held for the drive's
/// grace delay instead of raised; each <see cref="Tick"/> re-measures the files, and a held alert whose writers removed all but
/// 10 % of that growth is discarded, with its firings retracted from the drive's evaluator so it starts no cooldown. An
/// escalation on a drive with a held alert replaces it and is raised at once. After a raise, an alert with tracked growth is
/// watched for the drive's resolve window, and resolves itself (acknowledged, with a time and a reason) when its writers remove
/// the growth. Floor and process write volume alerts, and alerts with no tracked growth, are never held.
/// </para>
/// <para>
/// Held and watched alerts live in memory only: a service restart drops the held ones and stops watching the raised ones.
/// <see cref="Raise"/> and <see cref="Tick"/> run on the sampler thread only.
/// </para>
/// </remarks>
/// <param name="config">Supplies how many processes, folders and files an alert lists, and each drive's grace delay and resolve window.</param>
/// <param name="aggregators">Supplies who wrote to the drive within the write window.</param>
/// <param name="history">Stores the alert.</param>
/// <param name="hub">Pushes the alert to subscribed clients.</param>
/// <param name="sizes">Reads the current size of the files an alert lists.</param>
/// <param name="logger">Receives the alert at Warning, which reaches the Event Log.</param>
public sealed partial class AlertEngine(
    ConfigService config,
    WriteAggregatorProvider aggregators,
    HistoryStore history,
    StatusHub hub,
    IFileSizeProbe sizes,
    ILogger<AlertEngine> logger
)
{
    private static readonly TriggerKind[] Precedence =
    [
        TriggerKind.TimeToFull,
        TriggerKind.DropRate,
        TriggerKind.ProcessWriteVolume,
        TriggerKind.Floor,
    ];

    private readonly Dictionary<string, HeldAlert> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchedAlert> _watched = [];

    /// <summary>Raises, or holds, one alert for the firings of one evaluation of one drive.</summary>
    /// <remarks>
    /// The alert's trigger and reason come from the first firing present in the order time to full, drop rate, process write
    /// volume, floor; it is an escalation when any firing escalated. A ramp that crosses several thresholds at once therefore
    /// raises a single alert.
    /// </remarks>
    /// <param name="drive">The drive letter.</param>
    /// <param name="sample">The sample the firings came from.</param>
    /// <param name="writeWindowSamples">The drive's samples inside the write window, oldest first.</param>
    /// <param name="firings">The firings of that evaluation.</param>
    /// <param name="evaluator">The drive's evaluator, whose firings are retracted when a held alert is discarded.</param>
    /// <returns>The stored alert, or null when there were no firings or the alert is held.</returns>
    public Alert? Raise(
        string drive,
        DriveSample sample,
        IReadOnlyList<DriveSample> writeWindowSamples,
        IReadOnlyList<TriggerFiring> firings,
        TriggerEvaluator evaluator
    )
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(writeWindowSamples);
        ArgumentNullException.ThrowIfNull(firings);
        ArgumentNullException.ThrowIfNull(evaluator);
        if (firings.Count == 0)
        {
            return null;
        }

        Alert alert = BuildAlert(drive, sample, writeWindowSamples, firings);
        var growth = TrackedGrowth.From(alert);
        int graceSeconds = config.Current.For(alert.Drive).GraceSeconds;
        bool replacesHeld = alert.IsEscalation && _held.Remove(alert.Drive);
        if (replacesHeld || growth is null || graceSeconds <= 0 || alert.Trigger is not (TriggerKind.DropRate or TriggerKind.TimeToFull))
        {
            return Publish(alert, growth, sample.Time);
        }

        DateTimeOffset heldSince = _held.TryGetValue(alert.Drive, out HeldAlert? earlier) ? earlier.Since : sample.Time;
        TriggerKind[] kinds = [.. firings.Select(f => f.Kind).Distinct()];
        _held[alert.Drive] = new HeldAlert(alert, growth, kinds, evaluator, heldSince, TimeSpan.FromSeconds(graceSeconds));
        LogHeld(logger, alert.Drive, alert.Trigger, graceSeconds);
        return null;
    }

    /// <summary>Re-measures the files of held and watched alerts: discards, raises or resolves them.</summary>
    /// <remarks>
    /// A held alert whose remaining growth is 10 % or less is discarded; one whose grace delay has elapsed is raised. A watched
    /// alert whose remaining growth is 10 % or less is resolved; one whose resolve window has ended is no longer watched.
    /// </remarks>
    /// <param name="now">The time of the sampler tick.</param>
    public void Tick(DateTimeOffset now)
    {
        foreach (HeldAlert held in _held.Values.ToList())
        {
            CheckHeld(held, now);
        }

        foreach (WatchedAlert watched in _watched.ToList())
        {
            CheckWatched(watched, now);
        }
    }

    /// <summary>Computes the part of the observed free-space drop that traced file growth does not explain.</summary>
    /// <remarks>
    /// <c>UnattributedBytes = max(0, observedDrop - attributedGrowth)</c>, where <c>observedDrop</c> is the free bytes of the
    /// first sample inside the write window minus the free bytes of the last one, and <c>attributedGrowth</c> is the
    /// snapshot's <see cref="DriveWriteSnapshot.TotalExtendBytes"/> when it is positive, otherwise its
    /// <see cref="DriveWriteSnapshot.TotalBytesWritten"/>.
    /// </remarks>
    /// <param name="writeWindowSamples">The drive's samples inside the write window, oldest first.</param>
    /// <param name="snapshot">What was written to the drive within the window.</param>
    /// <returns>The unattributed bytes; 0 with fewer than two samples.</returns>
    internal static long UnattributedBytes(IReadOnlyList<DriveSample> writeWindowSamples, DriveWriteSnapshot snapshot)
    {
        if (writeWindowSamples.Count < 2)
        {
            return 0;
        }

        long observedDrop = writeWindowSamples[0].FreeBytes - writeWindowSamples[^1].FreeBytes;
        long attributedGrowth = snapshot.TotalExtendBytes > 0 ? snapshot.TotalExtendBytes : snapshot.TotalBytesWritten;
        return Math.Max(0, observedDrop - attributedGrowth);
    }

    private Alert BuildAlert(string drive, DriveSample sample, IReadOnlyList<DriveSample> writeWindowSamples, IReadOnlyList<TriggerFiring> firings)
    {
        TriggerFiring lead =
            Precedence.Select(kind => firings.FirstOrDefault(f => f.Kind == kind)).OfType<TriggerFiring>().FirstOrDefault() ?? firings[0];
        WatcherConfig settings = config.Current;
        string letter = drive.Trim().TrimEnd('\\', ':').ToUpperInvariant();
        DriveWriteSnapshot snapshot = aggregators.Current.Snapshot(
            letter[0],
            sample.Time,
            settings.TopProcesses,
            settings.TopFolders,
            settings.TopFiles
        );
        return new Alert
        {
            Id = Alert.CreateId(sample.Time, letter, lead.Kind),
            Time = sample.Time,
            Drive = letter,
            Trigger = lead.Kind,
            IsEscalation = firings.Any(f => f.IsEscalation),
            Reason = lead.Reason,
            FreeBytes = sample.FreeBytes,
            TotalBytes = sample.TotalBytes,
            DropRateBytesPerSecond = lead.DropRateBytesPerSecond,
            TimeToFull = lead.TimeToFull,
            UnattributedBytes = UnattributedBytes(writeWindowSamples, snapshot),
            Processes = [.. snapshot.Processes.Select(WithCurrentSizes)],
        };
    }

    private Alert Publish(Alert alert, TrackedGrowth? growth, DateTimeOffset raisedAt)
    {
        Alert saved = history.Save(alert);
        LogAlert(logger, saved.Id, saved.Reason, DescribeTopWriter(saved.Processes));
        hub.PublishAlert(saved);
        int resolveMinutes = config.Current.For(saved.Drive).ResolveMinutes;
        if (growth is not null && resolveMinutes > 0)
        {
            _watched.Add(new WatchedAlert(saved, growth, raisedAt + TimeSpan.FromMinutes(resolveMinutes)));
        }

        return saved;
    }

    private void CheckHeld(HeldAlert held, DateTimeOffset now)
    {
        GrowthCheck check = held.Growth.Check(RemainingGrowth);
        if (check.Cleared)
        {
            _held.Remove(held.Alert.Drive);
            string removed = ByteFormat.Format(check.RemovedBytes);
            LogDiscarded(logger, held.Alert.Drive, held.Alert.Trigger, removed);
            foreach (TriggerKind kind in held.Kinds)
            {
                held.Evaluator.Retract(kind);
            }
        }
        else if (now - held.Since >= held.Grace)
        {
            _held.Remove(held.Alert.Drive);
            Publish(held.Alert, held.Growth, now);
        }
    }

    private void CheckWatched(WatchedAlert watched, DateTimeOffset now)
    {
        if (now > watched.Until)
        {
            _watched.Remove(watched);
            return;
        }

        GrowthCheck check = watched.Growth.Check(RemainingGrowth);
        if (!check.Cleared)
        {
            return;
        }

        _watched.Remove(watched);
        string pronoun = check.Removers.Count == 1 ? "it" : "they";
        string reason = Invariant($"{string.Join(", ", check.Removers)} deleted {ByteFormat.Format(check.RemovedBytes)} {pronoun} had written");
        if (!history.MarkResolved(watched.Alert.Id, now, reason))
        {
            return;
        }

        Alert resolved = watched.Alert with { Acknowledged = true, ResolvedAt = now, ResolvedReason = reason };
        LogResolved(logger, resolved.Id, reason);
        hub.PublishAlertsChanged();
        hub.PublishAlertResolved(resolved);
    }

    private static string DescribeTopWriter(IReadOnlyList<ProcessWriteReport> processes) =>
        processes.Count == 0
            ? "no traced writer"
            : Invariant($"{processes[0].Name} (pid {processes[0].ProcessId}) wrote {ByteFormat.Format(processes[0].BytesWritten)}");

    private ProcessWriteReport WithCurrentSizes(ProcessWriteReport process) =>
        process with
        {
            Files = [.. process.Files.Select(file => file with { CurrentSize = CurrentSize(file.Path) })],
        };

    private long? CurrentSize(string path)
    {
        if (path.EndsWith(@"\*", StringComparison.Ordinal))
        {
            return null;
        }

        return TryReadSize(path, out long? size) ? size : null;
    }

    private long RemainingGrowth(TrackedFile file)
    {
        if (!TryReadSize(file.Path, out long? size))
        {
            return file.Growth;
        }

        return size is long current ? Math.Max(0, current - file.SizeBefore) : 0;
    }

    private bool TryReadSize(string path, out long? size)
    {
        try
        {
            size = sizes.SizeOf(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogSizeUnavailable(logger, path, ex.Message);
            size = null;
            return false;
        }
    }

    [LoggerMessage(EventId = 1300, Level = LogLevel.Warning, Message = "Alert {AlertId}: {Reason}. Top writer: {TopWriter}")]
    private static partial void LogAlert(ILogger logger, string alertId, string reason, string topWriter);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Debug, Message = "Could not read the size of {Path}: {Error}")]
    private static partial void LogSizeUnavailable(ILogger logger, string path, string error);

    [LoggerMessage(
        EventId = 1305,
        Level = LogLevel.Debug,
        Message = "Held alert {Drive} {Trigger} for up to {GraceSeconds} s: its writers may still remove what they wrote."
    )]
    private static partial void LogHeld(ILogger logger, string drive, TriggerKind trigger, int graceSeconds);

    [LoggerMessage(
        EventId = 1306,
        Level = LogLevel.Information,
        Message = "Held alert {Drive} {Trigger} discarded: its writers removed {Bytes} they had written."
    )]
    private static partial void LogDiscarded(ILogger logger, string drive, TriggerKind trigger, string bytes);

    [LoggerMessage(EventId = 1307, Level = LogLevel.Information, Message = "Alert {AlertId} resolved: {Reason}")]
    private static partial void LogResolved(ILogger logger, string alertId, string reason);

    /// <summary>An alert held for its grace delay.</summary>
    private sealed record HeldAlert(
        Alert Alert,
        TrackedGrowth Growth,
        TriggerKind[] Kinds,
        TriggerEvaluator Evaluator,
        DateTimeOffset Since,
        TimeSpan Grace
    );

    /// <summary>A raised alert watched until its resolve window ends.</summary>
    private sealed record WatchedAlert(Alert Alert, TrackedGrowth Growth, DateTimeOffset Until);
}
