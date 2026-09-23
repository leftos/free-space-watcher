using System.Diagnostics;
using System.IO;
using static System.FormattableString;

namespace FreeSpaceWatcher.Tray;

/// <summary>Where the tray records what it did and the failures it recovered from.</summary>
public interface ITrayLog
{
    /// <summary>Logs something worth knowing that is not a fault.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="exception">The exception behind it, or null.</param>
    void Information(string message, Exception? exception);

    /// <summary>Logs a failure the tray recovered from.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="exception">The exception behind it, or null.</param>
    void Warning(string message, Exception? exception);
}

/// <summary>The tray's append-only log file, rolled to the same path plus <c>.1</c> past 1 MB.</summary>
/// <remarks>Each entry is one line of timestamp, level and message, followed by the exception, if any. Safe to call from any thread.</remarks>
/// <param name="path">The log file's full path; the app passes <see cref="DefaultPath"/>.</param>
public sealed class TrayLog(string path) : ITrayLog
{
    /// <summary>The size past which the log is renamed to its path plus <c>.1</c> (replacing the previous one) and a new log begun.</summary>
    public const long RollSizeBytes = 1024 * 1024;

    private readonly Lock _gate = new();

    /// <summary>Gets the app's log path, <c>%LOCALAPPDATA%\FreeSpaceWatcher\tray.log</c>.</summary>
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreeSpaceWatcher", "tray.log");

    /// <summary>Gets the log file's full path.</summary>
    public string LogPath => path;

    /// <inheritdoc/>
    public void Information(string message, Exception? exception) => Write("INFO", message, exception);

    /// <inheritdoc/>
    public void Warning(string message, Exception? exception) => Write("WARN", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        string entry = Invariant($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}");
        if (exception is not null)
        {
            entry += exception + Environment.NewLine;
        }

        lock (_gate)
        {
            try
            {
                if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
                {
                    Directory.CreateDirectory(folder);
                }

                RollIfFull();
                File.AppendAllText(path, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not write to {path} ({ex.Message}); the entry was: {entry}");
            }
        }
    }

    private void RollIfFull()
    {
        FileInfo log = new(path);
        if (log.Exists && log.Length > RollSizeBytes)
        {
            File.Move(path, path + ".1", overwrite: true);
        }
    }
}
