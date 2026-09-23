using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Service.Config;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Alerts;

/// <summary>Deletes alerts older than the configured history length, at start and every 6 hours.</summary>
/// <param name="config">Supplies how many days of alerts to keep.</param>
/// <param name="history">The alert history.</param>
/// <param name="logger">Receives what was pruned and any failure.</param>
public sealed partial class HistoryPruner(ConfigService config, HistoryStore history, ILogger<HistoryPruner> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(1);

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan retryDelay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait = Interval;
            try
            {
                int deleted = history.Prune(DateTimeOffset.Now, config.Current.HistoryDays);
                LogPruned(logger, deleted);
                retryDelay = FirstRetryDelay;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogPruneFailed(logger, ex, retryDelay);
                wait = retryDelay;
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, Interval.Ticks));
            }

            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} old alerts from history")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not prune the alert history; retrying in {Delay}")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception, TimeSpan delay);
}
