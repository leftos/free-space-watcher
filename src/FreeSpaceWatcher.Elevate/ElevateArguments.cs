using System.Globalization;
using FreeSpaceWatcher.Native;

namespace FreeSpaceWatcher.Elevate;

/// <summary>One process action the helper was asked to run.</summary>
/// <param name="Action">What to do to the process.</param>
/// <param name="ProcessId">The process id.</param>
/// <param name="StartTime">When the tray saw the process start, or null when it did not know.</param>
internal sealed record ElevateRequest(ProcessControlAction Action, int ProcessId, DateTimeOffset? StartTime);

/// <summary>Parses the helper's command line: <c>suspend|resume|kill &lt;pid&gt; [&lt;startTimeUtcTicks&gt;]</c>.</summary>
internal static class ElevateArguments
{
    /// <summary>The usage line printed for a command line that does not parse.</summary>
    public const string Usage = "Usage: FreeSpaceWatcher.Elevate suspend|resume|kill <pid> [<startTimeUtcTicks>]";

    /// <summary>Parses the arguments strictly: a lowercase verb, a positive decimal pid, and optionally UTC ticks, nothing else.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The request, or null when the arguments do not match the usage.</returns>
    public static ElevateRequest? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count is < 2 or > 3 || ParseAction(args[0]) is not ProcessControlAction action || !TryParseProcessId(args[1], out int pid))
        {
            return null;
        }

        if (args.Count == 2)
        {
            return new ElevateRequest(action, pid, null);
        }

        return TryParseStartTime(args[2], out DateTimeOffset start) ? new ElevateRequest(action, pid, start) : null;
    }

    private static ProcessControlAction? ParseAction(string verb) =>
        verb switch
        {
            "suspend" => ProcessControlAction.Suspend,
            "resume" => ProcessControlAction.Resume,
            "kill" => ProcessControlAction.Terminate,
            _ => null,
        };

    private static bool TryParseProcessId(string text, out int processId) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out processId) && processId > 0;

    private static bool TryParseStartTime(string text, out DateTimeOffset start)
    {
        start = default;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return false;
        }

        start = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}
