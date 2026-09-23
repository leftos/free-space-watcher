using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>The words the tray reports a process action's outcome with, in the alerts window's status line and in toasts.</summary>
public static class ProcessActionText
{
    /// <summary>The name used for a process whose name a toast did not carry.</summary>
    public const string UnknownName = "process";

    /// <summary>The body of the toast that confirms a suspend.</summary>
    public const string SuspendedBody = "It stays frozen until you resume it.";

    /// <summary>The error shown when the service refused an action without saying why.</summary>
    public const string NoReason = "The service gave no reason.";

    /// <summary>Formats a successful action, e.g. "Suspended pwsh (pid 41372)".</summary>
    /// <param name="action">The action.</param>
    /// <param name="name">The process name.</param>
    /// <param name="processId">The process id.</param>
    /// <returns>The text, without a final period.</returns>
    public static string Succeeded(ProcessAction action, string name, int processId) => $"{PastTense(action)} {name} (pid {processId})";

    /// <summary>Formats a failed action's heading, e.g. "Suspend pwsh (pid 41372) failed".</summary>
    /// <param name="action">The action.</param>
    /// <param name="name">The process name.</param>
    /// <param name="processId">The process id.</param>
    /// <returns>The text, without the error.</returns>
    public static string Failed(ProcessAction action, string name, int processId) => $"{action} {name} (pid {processId}) failed";

    /// <summary>Formats the status line for an action's outcome.</summary>
    /// <param name="action">The action.</param>
    /// <param name="name">The process name.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="error">Why it failed, or null when it succeeded.</param>
    /// <returns>"Suspended pwsh (pid 41372)." on success, "Suspend pwsh (pid 41372) failed: {error}" on failure.</returns>
    public static string StatusLine(ProcessAction action, string name, int processId, string? error) =>
        error is null ? Succeeded(action, name, processId) + "." : $"{Failed(action, name, processId)}: {error}";

    /// <summary>Formats the status line after the user declined to retry a denied action as administrator.</summary>
    /// <param name="name">The process name.</param>
    /// <param name="processId">The process id.</param>
    /// <returns>The text.</returns>
    public static string Denied(string name, int processId) => $"Access to {name} (pid {processId}) was denied.";

    /// <summary>Reads why the service could not act, from its response.</summary>
    /// <param name="response">The service's response.</param>
    /// <returns>Null when the action succeeded; the service's error, or <see cref="NoReason"/>, when it did not.</returns>
    public static string? ErrorOf(ProcessActionResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Ok ? null : response.Error ?? NoReason;
    }

    private static string PastTense(ProcessAction action) =>
        action switch
        {
            ProcessAction.Suspend => "Suspended",
            ProcessAction.Resume => "Resumed",
            ProcessAction.Kill => "Killed",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };
}

/// <summary>Sends process actions to the service, logging each request and its response.</summary>
public static class ProcessActionSender
{
    /// <summary>Sends a process action and waits for the service's answer.</summary>
    /// <param name="channel">The service connection.</param>
    /// <param name="request">The action.</param>
    /// <param name="name">The process name, for the log.</param>
    /// <param name="log">Receives the request, the response or the failure.</param>
    /// <returns>The service's response.</returns>
    /// <exception cref="Exception">Any request failure of <see cref="IServiceChannel.SendAsync{TResponse}"/>, logged before it is rethrown.</exception>
    public static async Task<ProcessActionResponse> SendAsync(IServiceChannel channel, ProcessActionRequest request, string name, ITrayLog log)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(log);
        string subject = $"Process action {request.Action} on {name} (pid {request.ProcessId})";
        log.Information($"{subject} requested.", null);
        try
        {
            ProcessActionResponse response = await channel.SendAsync<ProcessActionResponse>(request, CancellationToken.None);
            string state = response.State?.ToString() ?? "unknown";
            log.Information(
                $"{subject} answered: ok={response.Ok}, accessDenied={response.AccessDenied}, state={state}, error={response.Error ?? "none"}.",
                null
            );
            return response;
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            log.Warning($"{subject} got no answer.", ex);
            throw;
        }
    }
}
