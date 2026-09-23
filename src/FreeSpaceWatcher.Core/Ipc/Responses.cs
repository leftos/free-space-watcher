using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Core.Ipc;

/// <summary>Every drive's live status, and whether write tracing is running.</summary>
/// <param name="Drives">One entry per known drive.</param>
/// <param name="EtwRunning">Whether the ETW write collector is running.</param>
/// <param name="EtwError">Why the collector is not running, when it is not.</param>
public sealed record StatusResponse(IReadOnlyList<DriveStatus> Drives, bool EtwRunning, string? EtwError) : PipeMessage;

/// <summary>One drive's live status.</summary>
public sealed record DriveStatus
{
    /// <summary>Gets the drive letter, e.g. "C".</summary>
    public required string Letter { get; init; }

    /// <summary>Gets whether the drive is watched.</summary>
    public required bool Watched { get; init; }

    /// <summary>Gets whether the drive is present and readable.</summary>
    public required bool Available { get; init; }

    /// <summary>Gets the free bytes.</summary>
    public required long FreeBytes { get; init; }

    /// <summary>Gets the drive's total size.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Gets the drop rate in bytes per second (positive = losing space), when known.</summary>
    public required double? DropRateBytesPerSecond { get; init; }

    /// <summary>Gets the time-to-full estimate, when known.</summary>
    public required TimeSpan? TimeToFull { get; init; }
}

/// <summary>The configuration in use.</summary>
/// <param name="Config">The configuration.</param>
/// <param name="LoadError">Why config.json could not be used at load time, if it could not.</param>
public sealed record ConfigResponse(WatcherConfig Config, string? LoadError) : PipeMessage;

/// <summary>The outcome of a configuration change.</summary>
/// <param name="Ok">Whether the configuration was saved and applied.</param>
/// <param name="Errors">The validation problems, when it was not.</param>
public sealed record SetConfigResponse(bool Ok, IReadOnlyList<string> Errors) : PipeMessage;

/// <summary>The alert history, newest first.</summary>
/// <param name="Alerts">One summary per alert.</param>
public sealed record AlertListResponse(IReadOnlyList<AlertSummary> Alerts) : PipeMessage;

/// <summary>The fields of an alert the history list shows.</summary>
public sealed record AlertSummary
{
    /// <summary>Gets the alert id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets when the alert was raised.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the drive letter.</summary>
    public required string Drive { get; init; }

    /// <summary>Gets the trigger that fired.</summary>
    public required TriggerKind Trigger { get; init; }

    /// <summary>Gets the one-sentence reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets whether the user acknowledged the alert.</summary>
    public required bool Acknowledged { get; init; }
}

/// <summary>One alert in full.</summary>
/// <param name="Alert">The alert, or null when no alert has the requested id.</param>
public sealed record AlertResponse(Alert? Alert) : PipeMessage;

/// <summary>The outcome of an acknowledgement.</summary>
/// <param name="Ok">Whether the alert exists and is now acknowledged.</param>
public sealed record AckResponse(bool Ok) : PipeMessage;

/// <summary>The outcome of a process action.</summary>
/// <param name="Ok">Whether the action succeeded.</param>
/// <param name="AccessDenied">Whether it failed because the caller may not act on the process.</param>
/// <param name="Error">What went wrong, when it failed.</param>
public sealed record ProcessActionResponse(bool Ok, bool AccessDenied, string? Error) : PipeMessage;

/// <summary>A new alert, pushed to subscribed clients.</summary>
/// <param name="Alert">The alert.</param>
public sealed record AlertPush(Alert Alert) : PipeMessage;

/// <summary>Live status, pushed to subscribed clients every second.</summary>
/// <param name="Status">The status.</param>
public sealed record StatusPush(StatusResponse Status) : PipeMessage;

/// <summary>A request the service could not handle.</summary>
/// <param name="Message">What went wrong.</param>
public sealed record ErrorResponse(string Message) : PipeMessage;
