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
/// <param name="config">Supplies how many processes, folders and files an alert lists.</param>
/// <param name="aggregators">Supplies who wrote to the drive within the write window.</param>
/// <param name="history">Stores the alert.</param>
/// <param name="hub">Pushes the alert to subscribed clients.</param>
/// <param name="logger">Receives the alert at Warning, which reaches the Event Log.</param>
public sealed partial class AlertEngine(
    ConfigService config,
    WriteAggregatorProvider aggregators,
    HistoryStore history,
    StatusHub hub,
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

    /// <summary>Raises one alert for the firings of one evaluation of one drive.</summary>
    /// <remarks>
    /// The alert's trigger and reason come from the first firing present in the order time to full, drop rate, process write
    /// volume, floor; it is an escalation when any firing escalated. A ramp that crosses several thresholds at once therefore
    /// raises a single alert.
    /// </remarks>
    /// <param name="drive">The drive letter.</param>
    /// <param name="sample">The sample the firings came from.</param>
    /// <param name="writeWindowSamples">The drive's samples inside the write window, oldest first.</param>
    /// <param name="firings">The firings of that evaluation.</param>
    /// <returns>The stored alert, or null when there were no firings.</returns>
    public Alert? Raise(string drive, DriveSample sample, IReadOnlyList<DriveSample> writeWindowSamples, IReadOnlyList<TriggerFiring> firings)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(writeWindowSamples);
        ArgumentNullException.ThrowIfNull(firings);
        if (firings.Count == 0)
        {
            return null;
        }

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
        Alert alert = new()
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

        Alert saved = history.Save(alert);
        LogAlert(logger, saved.Id, saved.Reason, DescribeTopWriter(saved.Processes));
        hub.PublishAlert(saved);
        return saved;
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

        try
        {
            FileInfo info = new(path);
            return info.Exists ? info.Length : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            LogSizeUnavailable(logger, path, ex.Message);
            return null;
        }
    }

    [LoggerMessage(EventId = 1300, Level = LogLevel.Warning, Message = "Alert {AlertId}: {Reason}. Top writer: {TopWriter}")]
    private static partial void LogAlert(ILogger logger, string alertId, string reason, string topWriter);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Debug, Message = "Could not read the size of {Path}: {Error}")]
    private static partial void LogSizeUnavailable(ILogger logger, string path, string error);
}
