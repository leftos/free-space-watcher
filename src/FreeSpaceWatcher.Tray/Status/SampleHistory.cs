using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>Keeps a drive's free-space history for the status window's sparkline: history loaded from the service plus live pushes.</summary>
public static class SampleHistory
{
    /// <summary>How much history the sparkline shows.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(600);

    /// <summary>Drops the samples older than <paramref name="span"/> before <paramref name="now"/>.</summary>
    /// <param name="samples">The samples, oldest first.</param>
    /// <param name="now">The time the span ends at.</param>
    /// <param name="span">How far back to keep.</param>
    /// <returns>The samples at or after <paramref name="now"/> minus <paramref name="span"/>, oldest first.</returns>
    public static IReadOnlyList<DriveSample> Trim(IReadOnlyList<DriveSample> samples, DateTimeOffset now, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(samples);
        DateTimeOffset oldest = now - span;
        return [.. samples.Where(s => s.Time >= oldest)];
    }

    /// <summary>Appends a live sample and trims the history to <paramref name="span"/> before it.</summary>
    /// <param name="samples">The history, oldest first.</param>
    /// <param name="sample">The new sample.</param>
    /// <param name="span">How far back to keep.</param>
    /// <returns>The new history, oldest first.</returns>
    public static IReadOnlyList<DriveSample> Append(IReadOnlyList<DriveSample> samples, DriveSample sample, TimeSpan span)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(sample);
        return Trim([.. samples, sample], sample.Time, span);
    }

    /// <summary>Puts history loaded from the service in front of the live samples that arrived while it loaded.</summary>
    /// <param name="loaded">The loaded history, oldest first.</param>
    /// <param name="live">The live samples, oldest first.</param>
    /// <param name="now">The time the span ends at.</param>
    /// <param name="span">How far back to keep.</param>
    /// <returns>The loaded samples older than the first live one, then the live ones, trimmed to <paramref name="span"/>.</returns>
    public static IReadOnlyList<DriveSample> Merge(
        IReadOnlyList<DriveSample> loaded,
        IReadOnlyList<DriveSample> live,
        DateTimeOffset now,
        TimeSpan span
    )
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(live);
        DateTimeOffset firstLive = live.Count > 0 ? live[0].Time : DateTimeOffset.MaxValue;
        return Trim([.. loaded.Where(s => s.Time < firstLive), .. live], now, span);
    }
}
