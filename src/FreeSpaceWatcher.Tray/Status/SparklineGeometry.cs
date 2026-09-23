using System.Windows;
using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>Where a sparkline's points and floor line go in its drawing area, and the free-space range they show.</summary>
/// <param name="Points">The line's points, oldest first; empty when there is nothing to draw.</param>
/// <param name="FloorY">The floor line's y, or null when the floor is outside the range in view.</param>
/// <param name="MinFreeBytes">The least free space among the samples in view, or null when there are none.</param>
/// <param name="MaxFreeBytes">The most free space among the samples in view, or null when there are none.</param>
public sealed record SparklineGeometry(IReadOnlyList<Point> Points, double? FloorY, long? MinFreeBytes, long? MaxFreeBytes)
{
    /// <summary>The fraction of the samples' range added above and below it.</summary>
    public const double Padding = 0.05;

    /// <summary>The smallest range shown, as a fraction of the drive's size.</summary>
    public const double MinimumRangeFraction = 0.005;

    /// <summary>The smallest range shown on any drive, 256 MiB.</summary>
    public const long MinimumRangeBytes = 256L << 20;

    /// <summary>
    /// Maps free-space samples into a drawing area: time runs left to right over <paramref name="span"/> ending at
    /// <paramref name="end"/>, and free space bottom to top over the samples' min to max plus <see cref="Padding"/> on each side,
    /// widened, centred on the data, to at least the larger of 0.5 % of the drive's size and 256 MiB so a steady drive draws flat.
    /// </summary>
    /// <param name="samples">The samples, oldest first; those outside the span are left out.</param>
    /// <param name="end">The time at the right edge.</param>
    /// <param name="span">The time from the left edge to the right edge.</param>
    /// <param name="size">The drawing area's size.</param>
    /// <param name="floorBytes">The drive's floor, drawn when it falls in the range shown; null for none.</param>
    /// <returns>The geometry; a single sample is drawn from its time to the right edge so it shows.</returns>
    public static SparklineGeometry Map(IReadOnlyList<DriveSample> samples, DateTimeOffset end, TimeSpan span, Size size, long? floorBytes)
    {
        ArgumentNullException.ThrowIfNull(samples);
        DateTimeOffset start = end - span;
        List<DriveSample> inView = [.. samples.Where(s => s.Time >= start && s.Time <= end)];
        if (inView.Count == 0 || span <= TimeSpan.Zero)
        {
            return new SparklineGeometry([], null, null, null);
        }

        long min = inView.Min(s => s.FreeBytes);
        long max = inView.Max(s => s.FreeBytes);
        (double low, double high) = Range(min, max, inView.Max(s => s.TotalBytes));
        double X(DateTimeOffset time) => (time - start) / span * size.Width;
        double Y(double value) => size.Height - ((value - low) / (high - low) * size.Height);

        List<Point> points = [.. inView.Select(s => new Point(X(s.Time), Y(s.FreeBytes)))];
        if (points.Count == 1)
        {
            points.Add(new Point(size.Width, points[0].Y));
        }

        double? floorY = floorBytes is long floor && floor >= low && floor <= high ? Y(floor) : null;
        return new SparklineGeometry(points, floorY, min, max);
    }

    private static (double Low, double High) Range(long min, long max, long totalBytes)
    {
        double pad = (max - min) * Padding;
        double low = min - pad;
        double high = max + pad;
        double minimum = Math.Max(totalBytes * MinimumRangeFraction, MinimumRangeBytes);
        if (high - low >= minimum)
        {
            return (low, high);
        }

        double centre = (min + (double)max) / 2;
        return (centre - (minimum / 2), centre + (minimum / 2));
    }
}
