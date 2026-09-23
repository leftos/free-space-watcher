using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Native;
using Microsoft.Extensions.Logging;
using static System.FormattableString;

namespace FreeSpaceWatcher.Service.Processes;

/// <summary>Suspends, resumes and kills processes for pipe clients, with the client's own rights.</summary>
/// <remarks>
/// The service's own process and critical processes are refused outright, and so is a request whose start time does not match
/// the live process (the pid was reused). Those checks run as the service; the action itself runs inside the caller's
/// impersonation, so a client can only act on processes its own token may open.
/// </remarks>
/// <param name="logger">Receives each action and its outcome.</param>
public sealed partial class ProcessActions(ILogger<ProcessActions> logger)
{
    /// <summary>How far a request's start time may differ from the live process's before the pid counts as reused.</summary>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(1);

    /// <summary>Checks and applies a process action.</summary>
    /// <param name="request">The action requested.</param>
    /// <param name="runAsClient">Runs its argument while impersonating the requesting client.</param>
    /// <returns>The outcome, without a request id.</returns>
    public ProcessActionResponse Handle(ProcessActionRequest request, Action<Action> runAsClient)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runAsClient);
        int pid = request.ProcessId;
        if (pid == Environment.ProcessId)
        {
            return Refuse("FreeSpaceWatcher does not act on its own process.");
        }

        ProcessControlResult query = ProcessControl.Query(pid, out ProcessFacts? facts);
        if (!query.Ok || facts is null)
        {
            return ToResponse(query);
        }

        if (facts.IsCritical)
        {
            return Refuse(Invariant($"Process {pid} is critical to Windows; ending or suspending it would stop the system."));
        }

        if (request.ProcessStartTime is DateTimeOffset expected && (facts.StartTime - expected).Duration() > StartTimeTolerance)
        {
            return Refuse(
                Invariant($"Process {pid} started at {facts.StartTime:yyyy-MM-dd HH:mm:ss zzz}, not at {expected:yyyy-MM-dd HH:mm:ss zzz}: ")
                    + "its id now belongs to a different process."
            );
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

    private static ProcessActionResponse Refuse(string reason) => new(false, false, reason);

    private static ProcessActionResponse ToResponse(ProcessControlResult result) => new(result.Ok, result.AccessDenied, result.Error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Process action {Action} on pid {ProcessId}: ok={Ok} {Error}")]
    private static partial void LogAction(ILogger logger, ProcessAction action, int processId, bool ok, string error);
}
