using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static System.FormattableString;

namespace FreeSpaceWatcher.Native;

/// <summary>What <see cref="ProcessControl.Apply"/> does to a process.</summary>
public enum ProcessControlAction
{
    /// <summary>Suspend every thread of the process.</summary>
    Suspend,

    /// <summary>Resume a suspended process.</summary>
    Resume,

    /// <summary>Terminate the process with exit code 1.</summary>
    Terminate,
}

/// <summary>Whether a process is running, suspended or gone, as <see cref="ProcessControl.QueryRunState"/> reads it.</summary>
public enum ProcessRunState
{
    /// <summary>At least one thread is not suspended.</summary>
    Running,

    /// <summary>The process has threads, and every one of them waits because it is suspended.</summary>
    Suspended,

    /// <summary>No process with the id is running, or it has no threads left.</summary>
    Exited,
}

/// <summary>The outcome of a native process call.</summary>
/// <param name="Ok">Whether the call succeeded.</param>
/// <param name="AccessDenied">Whether it failed because the calling token may not act on the process.</param>
/// <param name="Error">What went wrong, when it failed.</param>
public sealed record ProcessControlResult(bool Ok, bool AccessDenied, string? Error)
{
    /// <summary>Gets the result of a call that succeeded.</summary>
    public static ProcessControlResult Success { get; } = new(true, false, null);
}

/// <summary>What the process controls check before acting on a process.</summary>
/// <param name="StartTime">When the process started.</param>
/// <param name="IsCritical">Whether Windows marks the process critical (ending it stops the system).</param>
public sealed record ProcessFacts(DateTimeOffset StartTime, bool IsCritical);

/// <summary>Suspends, resumes and terminates processes with the calling thread's token, so impersonation decides access.</summary>
public static class ProcessControl
{
    private const int ShortPathCapacity = 1024;
    private const int LongPathCapacity = 32_768;

    /// <summary>The most resume calls <see cref="Apply"/> makes before it reports a process that stays suspended.</summary>
    public const int MaxResumeCalls = 32;

    /// <summary>The longest <see cref="WaitForRunState"/> waits in total before it returns the last state it read.</summary>
    private static readonly TimeSpan SettleBudget = TimeSpan.FromMilliseconds(500);

    /// <summary>The first pause <see cref="WaitForRunState"/> takes; each later pause is twice the one before it.</summary>
    private static readonly TimeSpan InitialSettlePause = TimeSpan.FromMilliseconds(5);

    /// <summary>Opens the process and applies <paramref name="action"/> to it.</summary>
    /// <remarks>
    /// Suspending an already suspended process does nothing and succeeds, so the per-thread suspend count never climbs above
    /// one through this call; the call returns once the process reports itself suspended, so a state read straight after it
    /// sees the suspend. Resuming calls the kernel until the process is no longer suspended, at most
    /// <see cref="MaxResumeCalls"/> times, so a process suspended several times by other tools runs again after one resume.
    /// </remarks>
    /// <param name="processId">The process id.</param>
    /// <param name="action">What to do.</param>
    /// <returns>Whether it worked, and why not when it did not.</returns>
    public static ProcessControlResult Apply(int processId, ProcessControlAction action)
    {
        const uint access = NativeMethods.ProcessSuspendResume | NativeMethods.ProcessTerminate | NativeMethods.ProcessQueryLimitedInformation;
        using SafeProcessHandle handle = NativeMethods.OpenProcess(access, false, processId);
        if (handle.IsInvalid)
        {
            return OpenFailure(processId, Marshal.GetLastPInvokeError());
        }

        return action switch
        {
            ProcessControlAction.Suspend => Suspend(handle, processId),
            ProcessControlAction.Resume => ResumeFully(handle, processId),
            ProcessControlAction.Terminate => NativeMethods.TerminateProcess(handle, 1)
                ? ProcessControlResult.Success
                : FromWin32(Marshal.GetLastPInvokeError(), "terminate", processId),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };
    }

    /// <summary>Reads whether a process is running, suspended or gone, from the system's thread list; needs no access to the process.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>
    /// <see cref="ProcessRunState.Suspended"/> when the process has threads and all of them wait suspended,
    /// <see cref="ProcessRunState.Exited"/> when no such process runs or it has no threads, and <see cref="ProcessRunState.Running"/> otherwise.
    /// </returns>
    public static ProcessRunState QueryRunState(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return RunStateOf(process);
        }
        catch (ArgumentException)
        {
            // GetProcessById throws ArgumentException when no process has the id.
            return ProcessRunState.Exited;
        }
    }

    /// <summary>
    /// Reads whether each of several processes is running, suspended or gone, from one snapshot of the system's process and
    /// thread lists; needs no access to the processes.
    /// </summary>
    /// <param name="processIds">The process ids; a repeated id is read once.</param>
    /// <returns>The state of each distinct id, as <see cref="QueryRunState"/> reads it; an id no process has reads as <see cref="ProcessRunState.Exited"/>.</returns>
    public static IReadOnlyDictionary<int, ProcessRunState> QueryRunStates(IReadOnlyCollection<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        Process[] snapshot = Process.GetProcesses();
        try
        {
            Dictionary<int, Process> byId = snapshot.ToDictionary(p => p.Id);
            Dictionary<int, ProcessRunState> states = [];
            foreach (int processId in processIds)
            {
                states[processId] = byId.TryGetValue(processId, out Process? process) ? RunStateOf(process) : ProcessRunState.Exited;
            }

            return states;
        }
        finally
        {
            foreach (Process process in snapshot)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>Polls a state until it is the one an action settles into, then returns the state it last read.</summary>
    /// <remarks>
    /// Suspending and resuming a process take effect thread by thread, and a thread reports its new state only once it has
    /// been scheduled and taken it, so a read straight after the call still sees the state from before it. The pauses
    /// double from <see cref="InitialSettlePause"/> and stop once <see cref="SettleBudget"/> of them have passed; a state
    /// that never settles is returned as it stands rather than failing the action.
    /// </remarks>
    /// <param name="readState">Reads the process state.</param>
    /// <param name="target">The settled state being waited for; <see cref="ProcessRunState.Exited"/> ends the wait too.</param>
    /// <param name="delay">Waits for the given time; a test passes a no-op to avoid real waits.</param>
    /// <returns>The last state read.</returns>
    public static ProcessRunState WaitForRunState(Func<ProcessRunState> readState, ProcessRunState target, Action<TimeSpan> delay)
    {
        ArgumentNullException.ThrowIfNull(readState);
        ArgumentNullException.ThrowIfNull(delay);
        ProcessRunState state = readState();
        TimeSpan remaining = SettleBudget;
        TimeSpan pause = InitialSettlePause;
        while (state != target && state != ProcessRunState.Exited && remaining > TimeSpan.Zero)
        {
            TimeSpan wait = pause < remaining ? pause : remaining;
            delay(wait);
            remaining -= wait;
            state = readState();
            pause += pause;
        }

        return state;
    }

    /// <summary>Reads a process's start time and whether it is critical.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="facts">The facts, when the process could be queried.</param>
    /// <returns>Whether the query worked, and why not when it did not.</returns>
    public static ProcessControlResult Query(int processId, out ProcessFacts? facts)
    {
        facts = null;
        using SafeProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            return OpenFailure(processId, Marshal.GetLastPInvokeError());
        }

        if (!NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _))
        {
            return FromWin32(Marshal.GetLastPInvokeError(), "read the start time of", processId);
        }

        if (!NativeMethods.IsProcessCritical(handle, out bool critical))
        {
            return FromWin32(Marshal.GetLastPInvokeError(), "check whether it may stop", processId);
        }

        facts = new ProcessFacts(DateTimeOffset.FromFileTime(created), critical);
        return ProcessControlResult.Success;
    }

    /// <summary>Reads the full path of a process's executable.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The executable's Win32 path, or null when the process cannot be opened (exited, protected, or access denied).</returns>
    public static string? TryGetImagePath(int processId)
    {
        using SafeProcessHandle handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle.IsInvalid)
        {
            return null;
        }

        foreach (int capacity in (int[])[ShortPathCapacity, LongPathCapacity])
        {
            char[] buffer = new char[capacity];
            uint length = (uint)capacity;
            if (NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref length))
            {
                return new string(buffer, 0, (int)length);
            }

            if (Marshal.GetLastPInvokeError() != NativeMethods.ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }

    private static ProcessRunState RunStateOf(Process process)
    {
        try
        {
            ProcessThreadCollection threads = process.Threads;
            if (threads.Count == 0)
            {
                return ProcessRunState.Exited;
            }

            foreach (ProcessThread thread in threads)
            {
                if (thread.ThreadState != System.Diagnostics.ThreadState.Wait || thread.WaitReason != ThreadWaitReason.Suspended)
                {
                    return ProcessRunState.Running;
                }
            }

            return ProcessRunState.Suspended;
        }
        catch (InvalidOperationException)
        {
            // The process exited between the lookup and the thread read.
            return ProcessRunState.Exited;
        }
    }

    private static ProcessControlResult Suspend(SafeProcessHandle handle, int processId)
    {
        if (QueryRunState(processId) == ProcessRunState.Suspended)
        {
            return ProcessControlResult.Success;
        }

        ProcessControlResult result = FromStatus(NativeMethods.NtSuspendProcess(handle), "suspend", processId);
        if (result.Ok)
        {
            WaitForRunState(() => QueryRunState(processId), ProcessRunState.Suspended, Thread.Sleep);
        }

        return result;
    }

    private static ProcessControlResult ResumeFully(SafeProcessHandle handle, int processId)
    {
        for (int calls = 1; ; calls++)
        {
            ProcessControlResult result = FromStatus(NativeMethods.NtResumeProcess(handle), "resume", processId);
            if (!result.Ok || QueryRunState(processId) != ProcessRunState.Suspended)
            {
                return result;
            }

            if (calls == MaxResumeCalls)
            {
                return new ProcessControlResult(
                    false,
                    false,
                    Invariant($"Process {processId} is still suspended after {MaxResumeCalls} resume calls.")
                );
            }
        }
    }

    private static ProcessControlResult OpenFailure(int processId, int error) =>
        error == NativeMethods.ErrorInvalidParameter
            ? new ProcessControlResult(false, false, Invariant($"No process with id {processId} is running."))
            : FromWin32(error, "open", processId);

    private static ProcessControlResult FromWin32(int error, string verb, int processId) =>
        error == NativeMethods.ErrorAccessDenied
            ? new ProcessControlResult(false, true, Invariant($"Access denied: this account may not {verb} process {processId}."))
            : new ProcessControlResult(false, false, Invariant($"Could not {verb} process {processId}: {new Win32Exception(error).Message}"));

    private static ProcessControlResult FromStatus(int status, string verb, int processId)
    {
        if (status >= 0)
        {
            return ProcessControlResult.Success;
        }

        if (status == NativeMethods.StatusAccessDenied)
        {
            return new ProcessControlResult(false, true, Invariant($"Access denied: this account may not {verb} process {processId}."));
        }

        string code = status.ToString("X8", CultureInfo.InvariantCulture);
        return new ProcessControlResult(false, false, Invariant($"Could not {verb} process {processId}: NTSTATUS 0x{code}."));
    }
}
