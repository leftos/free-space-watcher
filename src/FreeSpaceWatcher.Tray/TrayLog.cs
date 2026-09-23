using System.Diagnostics;
using System.IO;
using static System.FormattableString;

namespace FreeSpaceWatcher.Tray;

/// <summary>The tray's append-only log at <c>%LOCALAPPDATA%\FreeSpaceWatcher\tray.log</c>, rolled to <c>tray.log.1</c> past 1 MB.</summary>
/// <remarks>Each entry is one line of timestamp, level and message, followed by the exception, if any. Safe to call from any thread.</remarks>
public static class TrayLog
{
    /// <summary>The size past which the log is renamed to <c>tray.log.1</c> (replacing the previous one) and a new log begun.</summary>
    public const long RollSizeBytes = 1024 * 1024;

    private static readonly Lock Gate = new();
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeSpaceWatcher"
    );

    /// <summary>Gets the log file's full path.</summary>
    public static string LogPath { get; } = Path.Combine(Folder, "tray.log");

    /// <summary>Logs something worth knowing that is not a fault.</summary>
    /// <param name="message">What happened.</param>
    /// <param name="exception">The exception behind it, or null.</param>
    public static void Information(string message, Exception? exception) => Write("INFO", message, exception);

    /// <summary>Logs a failure the tray recovered from.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="exception">The exception behind it, or null.</param>
    public static void Warning(string message, Exception? exception) => Write("WARN", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        string entry = Invariant($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}");
        if (exception is not null)
        {
            entry += exception + Environment.NewLine;
        }

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                RollIfFull();
                File.AppendAllText(LogPath, entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Could not write to {LogPath} ({ex.Message}); the entry was: {entry}");
            }
        }
    }

    private static void RollIfFull()
    {
        FileInfo log = new(LogPath);
        if (log.Exists && log.Length > RollSizeBytes)
        {
            File.Move(LogPath, LogPath + ".1", overwrite: true);
        }
    }
}
