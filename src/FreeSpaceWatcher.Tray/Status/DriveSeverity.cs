using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>How worried a drive's free space should make the user; picks the free-space bar's colour.</summary>
public enum DriveSeverity
{
    /// <summary>At or above twice the floor, with no unacknowledged alert.</summary>
    Healthy,

    /// <summary>Below twice the floor but not below it.</summary>
    Caution,

    /// <summary>Below the floor, or the drive has an unacknowledged alert.</summary>
    Critical,
}

/// <summary>Decides a drive's <see cref="DriveSeverity"/>.</summary>
public static class DriveSeverityRules
{
    /// <summary>Gets a drive's floor in bytes: the larger of the byte floor and the percentage floor, as the floor trigger uses them.</summary>
    /// <param name="thresholds">The drive's thresholds.</param>
    /// <param name="totalBytes">The drive's size.</param>
    /// <returns>The floor.</returns>
    public static long FloorBytes(ResolvedThresholds thresholds, long totalBytes)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        return Math.Max(thresholds.FloorBytes, (long)(totalBytes * thresholds.FloorPercent / 100));
    }

    /// <summary>Decides a drive's severity; the floor applies whether or not the floor trigger is enabled.</summary>
    /// <param name="drive">The drive's status.</param>
    /// <param name="thresholds">The drive's thresholds.</param>
    /// <param name="hasUnacknowledgedAlert">Whether the drive has an alert the user has not acknowledged.</param>
    /// <returns>Critical for an unacknowledged alert or free space below the floor, Caution below twice the floor, else Healthy;
    /// an unavailable drive is Healthy unless it has an unacknowledged alert.</returns>
    public static DriveSeverity For(DriveStatus drive, ResolvedThresholds thresholds, bool hasUnacknowledgedAlert)
    {
        ArgumentNullException.ThrowIfNull(drive);
        if (hasUnacknowledgedAlert)
        {
            return DriveSeverity.Critical;
        }

        if (!drive.Available)
        {
            return DriveSeverity.Healthy;
        }

        long floor = FloorBytes(thresholds, drive.TotalBytes);
        return drive.FreeBytes < floor ? DriveSeverity.Critical
            : drive.FreeBytes < 2 * floor ? DriveSeverity.Caution
            : DriveSeverity.Healthy;
    }
}
