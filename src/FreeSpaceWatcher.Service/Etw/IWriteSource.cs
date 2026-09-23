namespace FreeSpaceWatcher.Service.Etw;

/// <summary>The state of the write collector, as the status snapshot reports it.</summary>
public interface IWriteSource
{
    /// <summary>Gets whether the ETW session is running and feeding the write aggregator.</summary>
    bool Running { get; }

    /// <summary>Gets why the session is not running, or null when it is (or has not started yet).</summary>
    string? ErrorMessage { get; }
}
