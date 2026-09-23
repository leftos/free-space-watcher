namespace FreeSpaceWatcher.Core.Config;

/// <summary>The complete thresholds for one drive, with every blank override filled from the defaults.</summary>
public sealed record ResolvedThresholds
{
    /// <summary>Gets the built-in defaults: 1 GB/min drop rate, 15 min to full, 50 MB/min noise floor, 5 GB / 5 % floor, 10 GB written.</summary>
    public static ResolvedThresholds Default { get; } =
        new()
        {
            DropRateBytesPerMinute = 1L << 30,
            TimeToFullMinutes = 15,
            NoiseFloorBytesPerMinute = 50L << 20,
            FloorBytes = 5L << 30,
            FloorPercent = 5,
            ProcessWriteBytes = 10L << 30,
            DropRateEnabled = true,
            TimeToFullEnabled = true,
            FloorEnabled = true,
            ProcessWriteEnabled = true,
        };

    /// <summary>Gets the free-space loss rate, in bytes per minute, at or above which the drop-rate trigger fires.</summary>
    public required long DropRateBytesPerMinute { get; init; }

    /// <summary>Gets the time-to-full estimate, in minutes, below which the time-to-full trigger fires.</summary>
    public required double TimeToFullMinutes { get; init; }

    /// <summary>Gets the loss rate, in bytes per minute, below which no time-to-full estimate is made.</summary>
    public required long NoiseFloorBytesPerMinute { get; init; }

    /// <summary>Gets the free-space floor in bytes.</summary>
    public required long FloorBytes { get; init; }

    /// <summary>Gets the free-space floor as a percentage of the drive's total size.</summary>
    public required double FloorPercent { get; init; }

    /// <summary>Gets the bytes one process may write to the drive within the write window before the trigger fires.</summary>
    public required long ProcessWriteBytes { get; init; }

    /// <summary>Gets whether the drop-rate trigger is enabled.</summary>
    public required bool DropRateEnabled { get; init; }

    /// <summary>Gets whether the time-to-full trigger is enabled.</summary>
    public required bool TimeToFullEnabled { get; init; }

    /// <summary>Gets whether the floor trigger is enabled.</summary>
    public required bool FloorEnabled { get; init; }

    /// <summary>Gets whether the process write volume trigger is enabled.</summary>
    public required bool ProcessWriteEnabled { get; init; }
}
