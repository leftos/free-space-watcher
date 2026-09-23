using System.IO;
using FreeSpaceWatcher.Core.Triggers;
using static System.FormattableString;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>Formats what the alerts window shows: relative times, trigger names, shortened paths and writer bars.</summary>
public static class AlertFormat
{
    /// <summary>The longest path a writer card or a folder or file row shows before it is shortened in the middle.</summary>
    public const int PathLength = 64;

    private const string Ellipsis = "…";

    /// <summary>Formats how long ago something happened.</summary>
    /// <param name="time">When it happened.</param>
    /// <param name="now">The current time.</param>
    /// <param name="zone">The time zone an older time is shown in.</param>
    /// <param name="culture">The culture an older time's month name is shown in.</param>
    /// <returns>
    /// "just now" under a minute (and for a time after <paramref name="now"/>), "N min ago" under an hour, "N h ago" under a day,
    /// else the date and time as "MMM d, HH:mm".
    /// </returns>
    public static string RelativeTime(DateTimeOffset time, DateTimeOffset now, TimeZoneInfo zone, IFormatProvider culture)
    {
        TimeSpan age = now - time;
        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return Invariant($"{(int)age.TotalMinutes} min ago");
        }

        return age.TotalDays < 1 ? Invariant($"{(int)age.TotalHours} h ago") : TimeZoneInfo.ConvertTime(time, zone).ToString("MMM d, HH:mm", culture);
    }

    /// <summary>Formats a time in full, for tooltips and the details header.</summary>
    /// <param name="time">The time.</param>
    /// <param name="zone">The time zone to show it in.</param>
    /// <param name="culture">The culture's short date and short time.</param>
    /// <returns>The time, e.g. "9/23/2026 2:31 PM".</returns>
    public static string AbsoluteTime(DateTimeOffset time, TimeZoneInfo zone, IFormatProvider culture) =>
        TimeZoneInfo.ConvertTime(time, zone).ToString("g", culture);

    /// <summary>Names a trigger the way the alerts window shows it.</summary>
    /// <param name="kind">The trigger.</param>
    /// <returns>"Drop rate", "Time to full", "Floor" or "Write volume".</returns>
    public static string TriggerName(TriggerKind kind) =>
        kind switch
        {
            TriggerKind.DropRate => "Drop rate",
            TriggerKind.TimeToFull => "Time to full",
            TriggerKind.Floor => "Floor",
            TriggerKind.ProcessWriteVolume => "Write volume",
            _ => kind.ToString(),
        };

    /// <summary>
    /// Shortens a path in the middle, keeping its root and its last segment and as many segments next to them as fit, taken from the
    /// end and the start in turn: <c>C:\Users\…\fsw-fill-test\fill.bin</c>.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="maxLength">The longest result wanted.</param>
    /// <returns>
    /// The path itself when it fits or has fewer than two segments; otherwise the shortened path, which is longer than
    /// <paramref name="maxLength"/> only when the root and the last segment alone are.
    /// </returns>
    public static string MiddleTrim(string path, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        if (path.Length <= maxLength)
        {
            return path;
        }

        string root = Path.GetPathRoot(path) ?? "";
        string[] segments = path[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            return path;
        }

        int head = 0;
        int tail = 1;
        bool fromEnd = true;
        while (head + tail < segments.Length - 1)
        {
            if (Fits(root, segments, fromEnd ? (head, tail + 1) : (head + 1, tail), maxLength))
            {
                (head, tail) = fromEnd ? (head, tail + 1) : (head + 1, tail);
            }
            else if (Fits(root, segments, fromEnd ? (head + 1, tail) : (head, tail + 1), maxLength))
            {
                (head, tail) = fromEnd ? (head + 1, tail) : (head, tail + 1);
            }
            else
            {
                break;
            }

            fromEnd = !fromEnd;
        }

        return Join(root, segments, head, tail);
    }

    /// <summary>Gets the length of a writer's bar: its bytes as a fraction of the top writer's.</summary>
    /// <param name="bytes">The writer's bytes.</param>
    /// <param name="topBytes">The top writer's bytes.</param>
    /// <returns>A fraction from 0 to 1; 0 when the top writer wrote nothing.</returns>
    public static double WriterFraction(long bytes, long topBytes) => topBytes <= 0 ? 0 : Math.Clamp((double)bytes / topBytes, 0, 1);

    /// <summary>Formats a writer's share of the top writer's bytes, for the caption beside the bar.</summary>
    /// <param name="bytes">The writer's bytes.</param>
    /// <param name="topBytes">The top writer's bytes.</param>
    /// <returns>"top writer" for the top writer, "&lt; 1 % of top writer" for a writer under one percent, else "N % of top writer".</returns>
    public static string WriterShareText(long bytes, long topBytes)
    {
        if (topBytes <= 0 || bytes >= topBytes)
        {
            return "top writer";
        }

        long percent = (long)((double)bytes / topBytes * 100);
        return percent < 1 ? "< 1 % of top writer" : Invariant($"{percent} % of top writer");
    }

    private static bool Fits(string root, string[] segments, (int Head, int Tail) kept, int maxLength) =>
        kept.Head + kept.Tail < segments.Length && Join(root, segments, kept.Head, kept.Tail).Length <= maxLength;

    private static string Join(string root, string[] segments, int head, int tail)
    {
        IEnumerable<string> parts = segments.Take(head).Append(Ellipsis).Concat(segments.TakeLast(tail));
        string prefix = root.Length == 0 || root.EndsWith('\\') ? root : root + "\\";
        return prefix + string.Join('\\', parts);
    }
}
