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

        // TerminateProcess returns before the process is gone, so a read straight after a kill could still see it running.
        ProcessState state = action == ProcessControlAction.Terminate && result.Ok ? ProcessState.Exited : QueryState(pid);
        return new ProcessActionResponse(result.Ok, result.AccessDenied, result.Error, state);
    }

    /// <summary>Reads whether each process is running, suspended or gone; a read-only query, so it runs as the service.</summary>
    /// <param name="processIds">The process ids.</param>
    /// <returns>The state of each distinct process id, without a request id.</returns>
    public static ProcessStatesResponse QueryStates(IReadOnlyList<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        return new ProcessStatesResponse(processIds.Distinct().ToDictionary(pid => pid, QueryState));
    }

    private static ProcessState QueryState(int processId) =>
        ProcessControl.QueryRunState(processId) switch
        {
            ProcessRunState.Running => ProcessState.Running,
            ProcessRunState.Suspended => ProcessState.Suspended,
            ProcessRunState.Exited => ProcessState.Exited,
            ProcessRunState state => throw new ArgumentOutOfRangeException(nameof(processId), state, "Unknown process run state."),
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
