using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Alerts;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Toasts;

/// <summary>The text of an alert toast and its progress bar, built from the alert alone so it can be checked without showing a toast.</summary>
/// <param name="Title">The hero line: the drive and the most urgent fact, e.g. "C: full in ~6 min".</param>
/// <param name="Body">The body line, e.g. "Losing 11.2 GB/min · 61.5 GB free".</param>
/// <param name="Attribution">The top writer and its top folder, e.g. "pwsh → C:\…\fsw-fill-test", or "No traced writer".</param>
/// <param name="ProgressTitle">The progress bar's title, the drive, e.g. "C:".</param>
/// <param name="UsedFraction">The drive's used fraction, from 0 to 1, the progress bar's value.</param>
/// <param name="ProgressValue">The text beside the progress bar, e.g. "61.5 GB free".</param>
/// <param name="ProgressStatus">The status under the progress bar: <see cref="CriticalStatus"/> or <see cref="LowStatus"/>.</param>
public sealed record AlertToastContent(
    string Title,
    string Body,
    string Attribution,
    string ProgressTitle,
    double UsedFraction,
    string ProgressValue,
    string ProgressStatus
)
{
    /// <summary>The longest the top folder is in the attribution line; longer paths are shortened in the middle.</summary>
    public const int FolderLength = 32;

    /// <summary>The attribution line when no process's writes were traced.</summary>
    public const string NoTracedWriter = "No traced writer";

    /// <summary>The progress status of a drive below its floor or on course to fill.</summary>
    public const string CriticalStatus = "Critical";

    /// <summary>The progress status of any other alerting drive.</summary>
    public const string LowStatus = "Low";

    /// <summary>Builds the toast text of an alert.</summary>
    /// <param name="alert">The alert.</param>
    /// <param name="floorBytes">The drive's floor (the larger of the byte and the percentage floor), or null when the configuration is not known.</param>
    /// <returns>The content.</returns>
    public static AlertToastContent From(Alert alert, long? floorBytes)
    {
        ArgumentNullException.ThrowIfNull(alert);
        string drive = $"{alert.Drive}:";
        string free = $"{ByteFormat.Format(alert.FreeBytes)} free";
        string body = alert.DropRateBytesPerSecond > 0 ? $"Losing {StatusText.FormatRate(alert.DropRateBytesPerSecond)} · {free}" : free;
        double used = alert.TotalBytes > 0 ? Math.Clamp((double)(alert.TotalBytes - alert.FreeBytes) / alert.TotalBytes, 0, 1) : 0;
        bool critical = alert.Trigger == TriggerKind.Floor || alert.TimeToFull is not null || alert.FreeBytes < floorBytes;
        return new AlertToastContent(
            TitleOf(alert, drive, floorBytes),
            body,
            AttributionOf(alert),
            drive,
            used,
            free,
            critical ? CriticalStatus : LowStatus
        );
    }

    private static string TitleOf(Alert alert, string drive, long? floorBytes) =>
        alert.Trigger switch
        {
            TriggerKind.Floor when floorBytes is long floor => $"{drive} below {ByteFormat.Format(floor)} floor",
            TriggerKind.Floor => $"{drive} below its floor",
            TriggerKind.ProcessWriteVolume when TopWriter(alert) is { } top => $"{drive} {top.Name} wrote {ByteFormat.Format(top.BytesWritten)}",
            TriggerKind.ProcessWriteVolume => alert.Reason,
            _ when alert.TimeToFull is TimeSpan eta => $"{drive} full in {StatusText.FormatEta(eta)}",
            _ => $"{drive} losing {StatusText.FormatRate(alert.DropRateBytesPerSecond)}",
        };

    private static string AttributionOf(Alert alert)
    {
        if (TopWriter(alert) is not { } top)
        {
            return NoTracedWriter;
        }

        return top.Folders.Count > 0 ? $"{top.Name} → {AlertFormat.MiddleTrim(top.Folders[0].Path, FolderLength)}" : top.Name;
    }

    private static ProcessWriteReport? TopWriter(Alert alert) => alert.Processes.Count > 0 ? alert.Processes[0] : null;
}
