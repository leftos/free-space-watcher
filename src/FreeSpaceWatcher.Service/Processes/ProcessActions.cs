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
            return ToResponse(check);
        }

        ProcessControlResult result = ProcessControlResult.Success;
        ProcessControlAction action = ToNative(request.Action);
        runAsClient(() => result = ProcessControl.Apply(pid, action));
        LogAction(logger, request.Action, pid, result.Ok, result.Error ?? "");
        return ToResponse(result);
    }

    private static ProcessControlAction ToNative(ProcessAction action) =>
        action switch
        {
            ProcessAction.Suspend => ProcessControlAction.Suspend,
            ProcessAction.Resume => ProcessControlAction.Resume,
            ProcessAction.Kill => ProcessControlAction.Terminate,
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };

    private static ProcessActionResponse ToResponse(ProcessControlResult result) => new(result.Ok, result.AccessDenied, result.Error);

    [LoggerMessage(EventId = 1500, Level = LogLevel.Information, Message = "Process action {Action} on pid {ProcessId}: ok={Ok} {Error}")]
    private static partial void LogAction(ILogger logger, ProcessAction action, int processId, bool ok, string error);
}
