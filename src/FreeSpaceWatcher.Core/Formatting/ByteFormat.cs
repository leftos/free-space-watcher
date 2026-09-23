using System.Globalization;

namespace FreeSpaceWatcher.Core.Formatting;

/// <summary>Formats byte counts for people: binary multiples (1 KB = 1024 B) with the familiar KB/MB/GB labels.</summary>
public static class ByteFormat
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    /// <summary>Formats a byte count with one decimal, e.g. 2254857830 becomes "2.1 GB" and 512 becomes "512 B".</summary>
    /// <param name="bytes">The byte count; negative values keep their sign.</param>
    /// <returns>The formatted size, culture-invariant.</returns>
    public static string Format(long bytes)
    {
        double value = Math.Abs((double)bytes);
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string sign = bytes < 0 ? "-" : "";
        string number = unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{sign}{number} {Units[unit]}";
    }
}
