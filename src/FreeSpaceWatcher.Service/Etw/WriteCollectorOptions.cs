namespace FreeSpaceWatcher.Service.Etw;

/// <summary>How the write collector names its ETW session and whose file events it ignores.</summary>
/// <param name="SessionName">The ETW session name; a stale session with this name is stopped at start.</param>
/// <param name="IgnoredProcessId">The pid whose file events are dropped: the service's own, so its history writes are not traced.</param>
public sealed record WriteCollectorOptions(string SessionName, int IgnoredProcessId)
{
    /// <summary>Gets the service's options: session "FreeSpaceWatcher", ignoring this process.</summary>
    public static WriteCollectorOptions ForService { get; } = new("FreeSpaceWatcher", Environment.ProcessId);
}
