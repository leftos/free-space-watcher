namespace FreeSpaceWatcher.Tray.Tests;

/// <summary>A tray log that keeps each entry in memory as "LEVEL message", so tests never touch the real tray.log.</summary>
internal sealed class FakeTrayLog : ITrayLog
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }

    public void Information(string message, Exception? exception) => Add("INFO", message);

    public void Warning(string message, Exception? exception) => Add("WARN", message);

    private void Add(string level, string message)
    {
        lock (_gate)
        {
            _lines.Add($"{level} {message}");
        }
    }
}
