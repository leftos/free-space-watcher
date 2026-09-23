namespace FreeSpaceWatcher.Core.Config;

/// <summary>Per-drive threshold overrides; a null field falls back to the configured default.</summary>
public sealed record Thresholds
{
    /// <summary>Gets the free-space loss rate, in bytes per minute, at or above which the drop-rate trigger fires.</summary>
    public long? DropRateBytesPerMinute { get; init; }

    /// <summary>Gets the time-to-full estimate, in minutes, below which the time-to-full trigger fires.</summary>
    public double? TimeToFullMinutes { get; init; }

    /// <summary>Gets the loss rate, in bytes per minute, below which no time-to-full estimate is made.</summary>
    public long? NoiseFloorBytesPerMinute { get; init; }

    /// <summary>Gets the free-space floor in bytes.</summary>
    public long? FloorBytes { get; init; }

    /// <summary>Gets the free-space floor as a percentage of the drive's total size.</summary>
    public double? FloorPercent { get; init; }

    /// <summary>Gets the bytes one process may write to the drive within the write window before the trigger fires.</summary>
    public long? ProcessWriteBytes { get; init; }

    /// <summary>Gets whether the drop-rate trigger is enabled.</summary>
    public bool? DropRateEnabled { get; init; }

    /// <summary>Gets whether the time-to-full trigger is enabled.</summary>
    public bool? TimeToFullEnabled { get; init; }

    /// <summary>Gets whether the floor trigger is enabled.</summary>
    public bool? FloorEnabled { get; init; }

    /// <summary>Gets whether the process write volume trigger is enabled.</summary>
    public bool? ProcessWriteEnabled { get; init; }

    /// <summary>Fills every blank field from <paramref name="defaults"/>.</summary>
    /// <param name="defaults">The values used where this instance has none.</param>
    /// <returns>The complete thresholds for a drive.</returns>
    public ResolvedThresholds ResolveOver(ResolvedThresholds defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        return new ResolvedThresholds
        {
            DropRateBytesPerMinute = DropRateBytesPerMinute ?? defaults.DropRateBytesPerMinute,
            TimeToFullMinutes = TimeToFullMinutes ?? defaults.TimeToFullMinutes,
            NoiseFloorBytesPerMinute = NoiseFloorBytesPerMinute ?? defaults.NoiseFloorBytesPerMinute,
            FloorBytes = FloorBytes ?? defaults.FloorBytes,
            FloorPercent = FloorPercent ?? defaults.FloorPercent,
            ProcessWriteBytes = ProcessWriteBytes ?? defaults.ProcessWriteBytes,
            DropRateEnabled = DropRateEnabled ?? defaults.DropRateEnabled,
            TimeToFullEnabled = TimeToFullEnabled ?? defaults.TimeToFullEnabled,
            FloorEnabled = FloorEnabled ?? defaults.FloorEnabled,
            ProcessWriteEnabled = ProcessWriteEnabled ?? defaults.ProcessWriteEnabled,
        };
    }
}
