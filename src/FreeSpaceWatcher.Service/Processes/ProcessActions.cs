using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Native;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Processes;

/// <summary>Suspends, resumes and kills processes for pipe clients, with the client's own rights.</summary>
/// <remarks>
/// The checks of <see cref="ProcessGuard"/> run as the service, protecting the service's own process; the action itself runs
/// inside the caller's impersonation, so a client can only act on processes its own token may open.
/// </remarks>
/// <param name="logger">Receives each action and its outcome.</param>
public sealed partial class ProcessActions(ILogger<ProcessActions> logger)
{
    /// <summary>Checks and applies a process action.</summary>
    /// <param name="request">The action requested.</param>
    /// <param name="runAsClient">Runs its argument while impersonating the requesting client.</param>
    /// <returns>The outcome, without a request id.</returns>
    public ProcessActionResponse Handle(ProcessActionRequest request, Action<Action> runAsClient)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runAsClient);
        int pid = request.ProcessId;
        ProcessControlResult check = ProcessGuard.Check(pid, request.ProcessStartTime, Environment.ProcessId);
        if (!check.Ok)
        {
            return new ProcessActionResponse(check.Ok, check.AccessDenied, check.Error, null);
        }

        ProcessControlResult result = ProcessControlResult.Success;
        ProcessControlAction action = ToNative(request.Action);
        runAsClient(() => result = ProcessControl.Apply(pid, action));
        LogAction(logger, request.Action, pid, result.Ok, result.Error ?? "");

        // TerminateProcess returns before the process is gone, and a suspend settles thread by thread, so a read straight
        // after either could still report the state from before the action.
        ProcessState state = result.Ok ? SettledState(pid, action) : QueryState(pid);
        return new ProcessActionResponse(result.Ok, result.AccessDenied, result.Error, state);
    }

    /// <summary>The most process ids one <see cref="GetProcessStatesRequest"/> may name.</summary>
    public const int MaxStateQueryProcessIds = 256;

    /// <summary>
    /// Reads whether each process is running, suspended or gone, from one snapshot of the system's processes; a read-only query,
    /// so it runs as the service.
    /// </summary>
    /// <param name="processIds">The process ids; at most <see cref="MaxStateQueryProcessIds"/>, repeats included.</param>
    /// <returns>
    /// The state of each distinct process id, or an <see cref="ErrorResponse"/> naming the limit when there are more than
    /// <see cref="MaxStateQueryProcessIds"/> ids; without a request id.
    /// </returns>
    public static PipeMessage QueryStates(IReadOnlyList<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        if (processIds.Count > MaxStateQueryProcessIds)
        {
            return new ErrorResponse(
                $"A process state request may name at most {MaxStateQueryProcessIds} processes; this one named {processIds.Count}."
            );
        }

        IReadOnlyDictionary<int, ProcessRunState> states = ProcessControl.QueryRunStates(processIds);
        return new ProcessStatesResponse(states.ToDictionary(entry => entry.Key, entry => ToState(entry.Value)));
    }

    private static ProcessState QueryState(int processId) => ToState(ProcessControl.QueryRunState(processId));

    /// <summary>Reads the state an action that succeeded has settled into.</summary>
    /// <param name="processId">The process id.</param>
    /// <param name="action">The action that just ran.</param>
    /// <returns>The settled state: a killed process is gone; a suspend or resume waits for its threads to take the change.</returns>
    private static ProcessState SettledState(int processId, ProcessControlAction action) =>
        action == ProcessControlAction.Terminate
            ? ProcessState.Exited
            : ToState(ProcessControl.WaitForRunState(() => ProcessControl.QueryRunState(processId), SettledActionState(action), Thread.Sleep));

    private static ProcessRunState SettledActionState(ProcessControlAction action) =>
        action switch
        {
            ProcessControlAction.Suspend => ProcessRunState.Suspended,
            ProcessControlAction.Resume => ProcessRunState.Running,
            ProcessControlAction.Terminate => ProcessRunState.Exited,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };

    private static ProcessState ToState(ProcessRunState state) =>
        state switch
        {
            ProcessRunState.Running => ProcessState.Running,
            ProcessRunState.Suspended => ProcessState.Suspended,
            ProcessRunState.Exited => ProcessState.Exited,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown process run state."),
        };

    private static ProcessControlAction ToNative(ProcessAction action) =>
        action switch
        {
            ProcessAction.Suspend => ProcessControlAction.Suspend,
            ProcessAction.Resume => ProcessControlAction.Resume,
            ProcessAction.Kill => ProcessControlAction.Terminate,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "Process action {Action} on pid {ProcessId}: ok={Ok} {Error}")]
    private static partial void LogAction(ILogger logger, ProcessAction action, int processId, bool ok, string error);
}
