using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using static System.FormattableString;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>Turns live drive status into the text the tray tooltip and the settings grid show.</summary>
public static class StatusText
{
    /// <summary>The tooltip while the service pipe is not connected.</summary>
    public const string ServiceNotRunning = "Service not running";

    /// <summary>The placeholder for a value that is not known.</summary>
    public const string Unknown = "—";

    /// <summary>The longest tooltip Windows shows: its buffer holds 128 characters, including the terminating null.</summary>
    public const int MaxTooltipLength = 127;

    /// <summary>Builds the tray tooltip: one line per watched drive, or <see cref="ServiceNotRunning"/> when disconnected.</summary>
    /// <param name="connected">Whether the service is connected.</param>
    /// <param name="status">The latest status, if any arrived.</param>
    /// <param name="config">The configuration in use, for each drive's noise floor; the built-in defaults apply without one.</param>
    /// <returns>The tooltip text, lines separated by "\n", cut to <see cref="MaxTooltipLength"/> characters ending in "…" when longer.</returns>
    public static string Tooltip(bool connected, StatusResponse? status, WatcherConfig? config)
    {
        if (!connected)
        {
            return ServiceNotRunning;
        }

        if (status is null)
        {
            return "Connecting to the service…";
        }

        List<string> lines = [.. status.Drives.Where(d => d.Watched).Select(d => DriveLine(d, NoiseFloor(config, d.Letter)))];
        return lines.Count == 0 ? "No drives are watched" : FitTooltip(string.Join('\n', lines));
    }

    /// <summary>Gets a drive's noise floor: its override, the configured default, or the built-in default without a configuration.</summary>
    /// <param name="config">The configuration, if loaded.</param>
    /// <param name="letter">The drive letter.</param>
    /// <returns>The noise floor in bytes per minute.</returns>
    public static long NoiseFloor(WatcherConfig? config, string letter) =>
        (config?.For(letter) ?? ResolvedThresholds.Default).NoiseFloorBytesPerMinute;

    /// <summary>Formats one drive's tooltip line, e.g. "C: 41.2 GB free · losing 1.3 GB/min · full in ~31 min".</summary>
    /// <param name="drive">The drive's status.</param>
    /// <param name="noiseFloorBytesPerMinute">The loss rate at or below which rate and ETA are left out.</param>
    /// <returns>The line.</returns>
    public static string DriveLine(DriveStatus drive, long noiseFloorBytesPerMinute)
    {
        ArgumentNullException.ThrowIfNull(drive);
        if (!drive.Available)
        {
            return $"{drive.Letter}: not available";
        }

        string line = $"{drive.Letter}: {ByteFormat.Format(drive.FreeBytes)} free";
        if (drive.DropRateBytesPerSecond is not double rate || !IsLosing(rate, noiseFloorBytesPerMinute))
        {
            return line;
        }

        line += $" · losing {FormatRate(rate)}";
        return drive.TimeToFull is TimeSpan eta ? $"{line} · full in {FormatEta(eta)}" : line;
    }

    /// <summary>Formats a rate given per second as a per-minute size, e.g. "1.3 GB/min".</summary>
    /// <param name="bytesPerSecond">The rate in bytes per second.</param>
    /// <returns>The formatted rate.</returns>
    public static string FormatRate(double bytesPerSecond) => $"{ByteFormat.Format((long)Math.Round(bytesPerSecond * 60))}/min";

    /// <summary>Formats a time-to-full estimate the way alert reasons do: "under a minute", "~31 min" or "~3 h".</summary>
    /// <param name="eta">The estimate.</param>
    /// <returns>The formatted estimate.</returns>
    public static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalMinutes < 1)
        {
            return "under a minute";
        }

        return eta.TotalMinutes < 120 ? Invariant($"~{Math.Round(eta.TotalMinutes):0} min") : Invariant($"~{Math.Round(eta.TotalHours):0} h");
    }

    /// <summary>Formats the settings grid's rate column: "losing …", "gaining …", "steady", or unknown.</summary>
    /// <param name="bytesPerSecond">The drop rate in bytes per second (positive = losing space), if known.</param>
    /// <param name="noiseFloorBytesPerMinute">The rate either way within which the drive counts as steady.</param>
    /// <returns>The column text.</returns>
    public static string RateColumn(double? bytesPerSecond, long noiseFloorBytesPerMinute)
    {
        if (bytesPerSecond is not double rate)
        {
            return Unknown;
        }

        if (IsLosing(rate, noiseFloorBytesPerMinute))
        {
            return $"losing {FormatRate(rate)}";
        }

        return IsLosing(-rate, noiseFloorBytesPerMinute) ? $"gaining {FormatRate(-rate)}" : "steady";
    }

    /// <summary>Formats the settings grid's ETA column: the estimate while losing faster than the noise floor, else unknown.</summary>
    /// <param name="drive">The drive's status.</param>
    /// <param name="noiseFloorBytesPerMinute">The drive's noise floor.</param>
    /// <returns>The column text.</returns>
    public static string EtaColumn(DriveStatus drive, long noiseFloorBytesPerMinute)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return drive.DropRateBytesPerSecond is double rate && IsLosing(rate, noiseFloorBytesPerMinute) && drive.TimeToFull is TimeSpan eta
            ? FormatEta(eta)
            : Unknown;
    }

    private static string FitTooltip(string text) =>
        text.Length <= MaxTooltipLength ? text : string.Concat(text.AsSpan(0, MaxTooltipLength - 1), "…");

    private static bool IsLosing(double bytesPerSecond, long noiseFloorBytesPerMinute) => bytesPerSecond * 60 > noiseFloorBytesPerMinute;
}
