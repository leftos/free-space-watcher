using static System.FormattableString;

namespace FreeSpaceWatcher.Native;

/// <summary>The checks a process action passes before it runs, shared by the service and the elevation helper.</summary>
/// <remarks>
/// A protected process (the caller's own) and critical processes are refused outright, and so is a request whose start time
/// does not match the live process, since its id was reused. A process that is no longer running fails the query.
/// </remarks>
public static class ProcessGuard
{
    /// <summary>How far a request's start time may differ from the live process's before the pid counts as reused.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Checks whether a process action on <paramref name="processId"/> may go ahead.</summary>
    /// <param name="processId">The process the action targets.</param>
    /// <param name="expectedStartTime">When the caller saw the process start, or null when it does not know.</param>
    /// <param name="protectedProcessId">The process that must never be acted on: the caller's own.</param>
    /// <returns>Success when the action may run; otherwise why not.</returns>
    public static ProcessControlResult Check(int processId, DateTimeOffset? expectedStartTime, int protectedProcessId)
    {
        if (processId == protectedProcessId)
        {
            return Refuse("FreeSpaceWatcher does not act on its own process.");
        }

        ProcessControlResult query = ProcessControl.Query(processId, out ProcessFacts? facts);
        if (!query.Ok || facts is null)
        {
            return query;
        }

        if (facts.IsCritical)
        {
            return Refuse(Invariant($"Process {processId} is critical to Windows; ending or suspending it would stop the system."));
        }

        if (expectedStartTime is DateTimeOffset expected && (facts.StartTime - expected).Duration() > StartTimeTolerance)
        {
            return Refuse(
                Invariant($"Process {processId} started at {facts.StartTime:yyyy-MM-dd HH:mm:ss zzz}, not at {expected:yyyy-MM-dd HH:mm:ss zzz}: ")
                    + "its id now belongs to a different process."
            );
        }

        return ProcessControlResult.Success;
    }

    private static ProcessControlResult Refuse(string reason) => new(false, false, reason);
}
