using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Toasts;

/// <summary>
/// Shows one alert toast built from the newest alert in the service's history, for <c>--test-toast</c>, so the toast can be looked
/// at without waiting for a real alert. It runs on its own connection, so it works whether or not a tray is already running.
/// </summary>
public static class TestToast
{
    /// <summary>The tray switch that shows the test toast and exits.</summary>
    public const string Switch = "--test-toast";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Connects to the service, fetches the newest alert and the configuration, and shows the alert's toast.</summary>
    /// <param name="log">Receives what was shown, or why nothing was.</param>
    /// <returns>0 when the toast was shown; 1 when the service did not answer or the history is empty.</returns>
    public static async Task<int> ShowNewestAsync(ITrayLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        await using PipeClient client = new(PipeProtocol.PipeName, PipeClient.DefaultRequestTimeout, log);
        TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += (_, isConnected) =>
        {
            if (isConnected)
            {
                connected.TrySetResult();
            }
        };
        client.Start();
        if (!client.IsConnected && await Task.WhenAny(connected.Task, Task.Delay(ConnectTimeout)) != connected.Task)
        {
            log.Warning($"{Switch}: the service did not answer within {ConnectTimeout.TotalSeconds:0} s; no toast shown.", null);
            return 1;
        }

        try
        {
            AlertListResponse list = await client.SendAsync<AlertListResponse>(new ListAlertsRequest(), CancellationToken.None);
            if (list.Alerts.OrderByDescending(a => a.Time).FirstOrDefault() is not { } newest)
            {
                log.Warning($"{Switch}: the alert history is empty; no toast shown.", null);
                return 1;
            }

            AlertResponse response = await client.SendAsync<AlertResponse>(new GetAlertRequest(newest.Id), CancellationToken.None);
            if (response.Alert is not Alert alert)
            {
                log.Warning($"{Switch}: alert {newest.Id} is no longer in the history; no toast shown.", null);
                return 1;
            }

            ConfigResponse config = await client.SendAsync<ConfigResponse>(new GetConfigRequest(), CancellationToken.None);
            AlertToasts.ShowAlert(alert, AlertToasts.FloorBytes(config.Config, alert));
            log.Information($"{Switch}: showed the toast of alert {alert.Id}.", null);
            return 0;
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            log.Warning($"{Switch}: a request to the service failed; no toast shown.", ex);
            return 1;
        }
    }
}
