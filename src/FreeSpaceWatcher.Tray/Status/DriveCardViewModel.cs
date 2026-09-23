using System.IO;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>One drive's card in the status window: free space, the bar, the rate line and the sparkline's history.</summary>
/// <remarks>Every member is called on the UI thread. The drive's volume label is read once, when the card is created.</remarks>
/// <param name="letter">The drive letter, e.g. "C".</param>
public sealed partial class DriveCardViewModel(string letter) : ObservableObject
{
    /// <summary>Gets the drive letter, e.g. "C".</summary>
    public string Letter { get; } = letter;

    /// <summary>Gets the drive's name as the header shows it, e.g. "C:".</summary>
    public string Title => Letter + ":";

    /// <summary>Gets the drive's volume label, or an empty string when it has none or it could not be read.</summary>
    public string VolumeLabel { get; } = ReadVolumeLabel(letter);

    /// <summary>Gets whether the drive is watched.</summary>
    [ObservableProperty]
    public partial bool IsWatched { get; private set; }

    /// <summary>Gets whether the drive is present and readable.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>Gets the free space, e.g. "41.2 GB".</summary>
    [ObservableProperty]
    public partial string FreeText { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the drive's size, e.g. "931.5 GB".</summary>
    [ObservableProperty]
    public partial string TotalText { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the used fraction of the drive, from 0 to 1.</summary>
    [ObservableProperty]
    public partial double UsedFraction { get; private set; }

    /// <summary>Gets the bar's severity.</summary>
    [ObservableProperty]
    public partial DriveSeverity Severity { get; private set; }

    /// <summary>Gets the rate line, e.g. "Losing 11.2 GB/min · full in ~6 min" or "Steady".</summary>
    [ObservableProperty]
    public partial string RateText { get; private set; } = "";

    /// <summary>Gets whether the drive is losing space faster than its noise floor, which colours the rate line.</summary>
    [ObservableProperty]
    public partial bool IsLosing { get; private set; }

    /// <summary>Gets the drive's floor in bytes, for the sparkline's floor line.</summary>
    [ObservableProperty]
    public partial long? FloorBytes { get; private set; }

    /// <summary>Gets the free-space history the sparkline draws, oldest first.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DriveSample> History { get; private set; } = [];

    /// <summary>Shows a drive's new status.</summary>
    /// <param name="drive">The drive's status.</param>
    /// <param name="thresholds">The drive's thresholds, for the floor and the noise floor.</param>
    /// <param name="hasUnacknowledgedAlert">Whether the drive has an unacknowledged alert.</param>
    /// <param name="sampleTime">The time to record the status at in the history, or null when it is not a new sample.</param>
    public void Update(DriveStatus drive, ResolvedThresholds thresholds, bool hasUnacknowledgedAlert, DateTimeOffset? sampleTime)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(thresholds);
        IsWatched = drive.Watched;
        IsAvailable = drive.Available;
        FreeText = ByteFormat.Format(drive.FreeBytes);
        TotalText = ByteFormat.Format(drive.TotalBytes);
        UsedFraction = drive.TotalBytes > 0 ? 1 - ((double)drive.FreeBytes / drive.TotalBytes) : 0;
        Severity = DriveSeverityRules.For(drive, thresholds, hasUnacknowledgedAlert);
        FloorBytes = DriveSeverityRules.FloorBytes(thresholds, drive.TotalBytes);
        RateText = StatusText.RateLine(drive, thresholds.NoiseFloorBytesPerMinute);
        IsLosing = drive.DropRateBytesPerSecond is double rate && StatusText.IsLosing(rate, thresholds.NoiseFloorBytesPerMinute);
        if (sampleTime is DateTimeOffset time && drive.Available)
        {
            History = SampleHistory.Append(History, new DriveSample(time, drive.FreeBytes, drive.TotalBytes), SampleHistory.Span);
        }
    }

    /// <summary>Puts history loaded from the service in front of the samples that arrived while it loaded.</summary>
    /// <param name="loaded">The loaded samples, oldest first.</param>
    /// <param name="now">The current time.</param>
    public void MergeHistory(IReadOnlyList<DriveSample> loaded, DateTimeOffset now) =>
        History = SampleHistory.Merge(loaded, History, now, SampleHistory.Span);

    /// <summary>Reads a drive's volume label; the label is decoration, so a drive that is not ready or cannot be queried has none.</summary>
    /// <param name="letter">The drive letter, e.g. "C".</param>
    /// <returns>The label, or an empty string.</returns>
    internal static string ReadVolumeLabel(string letter)
    {
        try
        {
            return new DriveInfo(letter).VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or SecurityException)
        {
            return "";
        }
    }
}
