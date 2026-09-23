using System.Globalization;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;

namespace FreeSpaceWatcher.Core.Alerts;

/// <summary>A raised alert: what fired, the drive's state, and who was writing.</summary>
public sealed record Alert
{
    /// <summary>Gets the alert id, <c>yyyyMMdd-HHmmss-letter-kind</c>, with -2, -3 appended by the history store on collision.</summary>
    public required string Id { get; init; }

    /// <summary>Gets when the alert was raised.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the drive letter, e.g. "C".</summary>
    public required string Drive { get; init; }

    /// <summary>Gets the trigger that fired.</summary>
    public required TriggerKind Trigger { get; init; }

    /// <summary>Gets whether the alert is an escalation that bypassed the cooldown.</summary>
    public required bool IsEscalation { get; init; }

    /// <summary>Gets the one-sentence reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the free bytes when the alert was raised.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Gets the drive's total size.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Gets the drop rate in bytes per second; positive means losing space.</summary>
    public required double DropRateBytesPerSecond { get; init; }

    /// <summary>Gets the time-to-full estimate, when known.</summary>
    public required TimeSpan? TimeToFull { get; init; }

    /// <summary>Gets the part of the observed drop that no traced file growth explains.</summary>
    public required long UnattributedBytes { get; init; }

    /// <summary>Gets the top writers to the drive within the write window.</summary>
    public required IReadOnlyList<ProcessWriteReport> Processes { get; init; }

    /// <summary>Gets whether the user acknowledged the alert.</summary>
    public bool Acknowledged { get; init; }

    /// <summary>Builds the base id for an alert, e.g. "20260923-141500-C-DropRate".</summary>
    /// <param name="time">When the alert was raised, formatted in its own offset.</param>
    /// <param name="drive">The drive letter.</param>
    /// <param name="trigger">The trigger that fired.</param>
    /// <returns>The id before any collision suffix.</returns>
    public static string CreateId(DateTimeOffset time, string drive, TriggerKind trigger) =>
        string.Create(CultureInfo.InvariantCulture, $"{time:yyyyMMdd-HHmmss}-{drive}-{trigger}");
}
