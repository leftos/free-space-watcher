using FreeSpaceWatcher.Core.Config;

namespace FreeSpaceWatcher.Core.Ipc;

/// <summary>Asks for every drive's live status.</summary>
public sealed record GetStatusRequest : PipeMessage;

/// <summary>Asks for the configuration in use.</summary>
public sealed record GetConfigRequest : PipeMessage;

/// <summary>Replaces the configuration.</summary>
/// <param name="Config">The new configuration.</param>
public sealed record SetConfigRequest(WatcherConfig Config) : PipeMessage;

/// <summary>Asks the service to push status every second and every new alert.</summary>
public sealed record SubscribeRequest : PipeMessage;

/// <summary>Asks for the alert history.</summary>
public sealed record ListAlertsRequest : PipeMessage;

/// <summary>Asks for one alert in full.</summary>
/// <param name="Id">The alert id.</param>
public sealed record GetAlertRequest(string Id) : PipeMessage;

/// <summary>Marks an alert as acknowledged.</summary>
/// <param name="Id">The alert id.</param>
public sealed record AckAlertRequest(string Id) : PipeMessage;

/// <summary>What to do to a process.</summary>
public enum ProcessAction
{
    /// <summary>Suspend every thread of the process.</summary>
    Suspend,

    /// <summary>Resume a suspended process.</summary>
    Resume,

    /// <summary>Terminate the process.</summary>
    Kill,
}

/// <summary>Asks the service to suspend, resume or kill a process.</summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="ProcessStartTime">The process start time, when known, so a reused pid is not acted on.</param>
/// <param name="Action">The action.</param>
public sealed record ProcessActionRequest(int ProcessId, DateTimeOffset? ProcessStartTime, ProcessAction Action) : PipeMessage;
