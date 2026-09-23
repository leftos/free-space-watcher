namespace FreeSpaceWatcher.Core.Triggers;

/// <summary>One free-space reading of a drive.</summary>
/// <param name="Time">When the reading was taken.</param>
/// <param name="FreeBytes">Bytes free for the caller.</param>
/// <param name="TotalBytes">The drive's total size in bytes.</param>
public sealed record DriveSample(DateTimeOffset Time, long FreeBytes, long TotalBytes);

/// <summary>The four conditions that raise an alert.</summary>
public enum TriggerKind
{
    /// <summary>Free space is falling faster than the drop-rate threshold.</summary>
    DropRate,

    /// <summary>At the current drop rate the drive fills sooner than the time-to-full threshold.</summary>
    TimeToFull,

    /// <summary>Free space is below the floor.</summary>
    Floor,

    /// <summary>One process grew its files on the drive by more than the threshold within the write window.</summary>
    ProcessWriteVolume,
}

/// <summary>How much one process grew its files on a drive within the write window.</summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="ProcessName">The process name.</param>
/// <param name="NetGrowthBytes">End-of-file growth minus the bytes the process deleted or truncated within the window, never below 0.</param>
public sealed record ProcessWriteTotal(int ProcessId, string ProcessName, long NetGrowthBytes);

/// <summary>A trigger that fired on one sample.</summary>
public sealed record TriggerFiring
{
    /// <summary>Gets which trigger fired.</summary>
    public required TriggerKind Kind { get; init; }

    /// <summary>Gets whether the condition worsened enough to bypass the cooldown.</summary>
    public required bool IsEscalation { get; init; }

    /// <summary>Gets the drop rate in bytes per second; positive means the drive is losing space, 0 when unknown.</summary>
    public required double DropRateBytesPerSecond { get; init; }

    /// <summary>Gets the time-to-full estimate, or null when the drop rate is unknown or below the noise floor.</summary>
    public required TimeSpan? TimeToFull { get; init; }

    /// <summary>Gets one sentence describing the condition, e.g. "C: losing 2.1 GB/min, full in ~9 min".</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the process that crossed the write volume threshold, for that trigger only.</summary>
    public int? ProcessId { get; init; }

    /// <summary>Gets the name of the process that crossed the write volume threshold, for that trigger only.</summary>
    public string? ProcessName { get; init; }
}
