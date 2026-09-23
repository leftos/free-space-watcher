using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Processes;
using FreeSpaceWatcher.Service.Sampling;
using FreeSpaceWatcher.Service.Status;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Pipe;

/// <summary>Answers one pipe request; subscriptions are the pipe server's, since they belong to the connection.</summary>
/// <param name="config">Answers configuration reads and changes.</param>
/// <param name="history">Answers alert history requests.</param>
/// <param name="hub">Supplies the latest status, and tells subscribers when an acknowledgement or deletion changed the history.</param>
/// <param name="processes">Handles process actions.</param>
/// <param name="samples">Answers drive history requests from the sampler's windows.</param>
/// <param name="logger">Receives configuration saves that fail, acknowledgements and deletions.</param>
public sealed partial class PipeRequestHandler(
    ConfigService config,
    HistoryStore history,
    StatusHub hub,
    ProcessActions processes,
    RecentSamples samples,
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
            AckAlertsRequest ack => new AckResponse(Acknowledge(ack.Ids)),
            DeleteAlertsRequest delete => new DeleteAlertsResponse(Delete(delete.Ids)),
            ProcessActionRequest action => processes.Handle(action, runAsClient),
            GetProcessStatesRequest states => ProcessActions.QueryStates(states.ProcessIds),
            GetDriveHistoryRequest drive => DriveHistory(drive),
            _ => new ErrorResponse($"'{request.GetType().Name}' is not a request the service handles."),
        };
        return response with { RequestId = request.RequestId };
    }

    private DriveHistoryResponse DriveHistory(GetDriveHistoryRequest request)
    {
        var span = TimeSpan.FromSeconds(Math.Min(request.Seconds, GetDriveHistoryRequest.MaxSeconds));
        return new DriveHistoryResponse(request.Letter, samples.Get(request.Letter, span));
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
            ResolvedAt = alert.ResolvedAt,
        };

    private int Acknowledge(IReadOnlyList<string>? ids)
    {
        int count = history.Acknowledge(ids);
        LogAcknowledged(logger, count);
        PublishIfChanged(count);
        return count;
    }

    private int Delete(IReadOnlyList<string>? ids)
    {
        int count = history.Delete(ids);
        LogDeleted(logger, count);
        PublishIfChanged(count);
        return count;
    }

    private void PublishIfChanged(int count)
    {
        if (count > 0)
        {
            hub.PublishAlertsChanged();
        }
    }

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

    [LoggerMessage(EventId = 1408, Level = LogLevel.Information, Message = "Acknowledged {Count} alerts")]
    private static partial void LogAcknowledged(ILogger logger, int count);

    [LoggerMessage(EventId = 1409, Level = LogLevel.Information, Message = "Deleted {Count} alerts")]
    private static partial void LogDeleted(ILogger logger, int count);
}
