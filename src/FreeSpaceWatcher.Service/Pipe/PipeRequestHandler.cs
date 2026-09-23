using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Processes;
using FreeSpaceWatcher.Service.Status;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Pipe;

/// <summary>Answers one pipe request; subscriptions are the pipe server's, since they belong to the connection.</summary>
/// <param name="config">Answers configuration reads and changes.</param>
/// <param name="history">Answers alert history requests.</param>
/// <param name="hub">Supplies the latest status.</param>
/// <param name="processes">Handles process actions.</param>
/// <param name="logger">Receives configuration saves that fail.</param>
public sealed partial class PipeRequestHandler(
    ConfigService config,
    HistoryStore history,
    StatusHub hub,
    ProcessActions processes,
    ILogger<PipeRequestHandler> logger
)
{
    /// <summary>Handles a request and returns its response, carrying the request's id.</summary>
    /// <param name="request">The request.</param>
    /// <param name="runAsClient">Runs its argument while impersonating the requesting client.</param>
    /// <returns>The response, or an <see cref="ErrorResponse"/> for a message that is not a request.</returns>
    public PipeMessage Handle(PipeMessage request, Action<Action> runAsClient)
    {
        ArgumentNullException.ThrowIfNull(request);
        PipeMessage response = request switch
        {
            GetStatusRequest => hub.Current,
            GetConfigRequest => new ConfigResponse(config.Current, config.LoadError),
            SetConfigRequest set => SetConfig(set.Config),
            ListAlertsRequest => new AlertListResponse([.. history.List().Select(Summarize)]),
            GetAlertRequest get => new AlertResponse(history.Get(get.Id)),
            AckAlertRequest ack => new AckResponse(history.Acknowledge(ack.Id)),
            ProcessActionRequest action => processes.Handle(action, runAsClient),
            _ => new ErrorResponse($"'{request.GetType().Name}' is not a request the service handles."),
        };
        return response with { RequestId = request.RequestId };
    }

    private static AlertSummary Summarize(Alert alert) =>
        new()
        {
            Id = alert.Id,
            Time = alert.Time,
            Drive = alert.Drive,
            Trigger = alert.Trigger,
            Reason = alert.Reason,
            Acknowledged = alert.Acknowledged,
        };

    private SetConfigResponse SetConfig(WatcherConfig newConfig)
    {
        try
        {
            IReadOnlyList<string> errors = config.Update(newConfig);
            return new SetConfigResponse(errors.Count == 0, errors);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(logger, ex);
            return new SetConfigResponse(false, ["The configuration could not be saved: " + ex.Message]);
        }
    }

    [LoggerMessage(EventId = 1400, Level = LogLevel.Warning, Message = "Saving the configuration failed")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
