namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Recognises the IRP write that retries a refused fast-I/O write, so a cached write is counted once.</summary>
/// <remarks>
/// When the fast-I/O path of a cached write is refused, the kernel logs the attempt (IoFlags 0) and then the IRP that retries
/// it on the same thread, with the same file object, offset and size. The filter remembers each thread's last fast-I/O
/// attempt and reports the next write on that thread as a retry when it carries IRP flags and repeats the attempt. The
/// IRP pointer is not part of the match: it differs between the two events of some pairs. At most <see cref="Capacity"/>
/// threads are remembered; past that the memory is cleared.
/// </remarks>
internal sealed class FastIoRetryFilter
{
    /// <summary>The most threads whose last fast-I/O attempt is remembered at once.</summary>
    public const int Capacity = 65_536;

    private readonly Dictionary<(int ProcessId, int ThreadId), Attempt> _lastAttempt = [];

    /// <summary>Tells whether a write event is the IRP retry of the fast-I/O attempt logged just before it on the same thread.</summary>
    /// <param name="processId">The writing process.</param>
    /// <param name="threadId">The writing thread.</param>
    /// <param name="fileObject">The kernel file object written through.</param>
    /// <param name="offset">The write's byte offset in the file.</param>
    /// <param name="ioSize">The write's size in bytes.</param>
    /// <param name="ioFlags">The event's IoFlags: 0 for a fast-I/O attempt, the IRP flags otherwise.</param>
    /// <returns>True when the event repeats the thread's remembered attempt and should be dropped.</returns>
    public bool IsRetry(int processId, int threadId, ulong fileObject, long offset, int ioSize, int ioFlags)
    {
        (int, int) key = (processId, threadId);
        Attempt write = new(fileObject, offset, ioSize);
        if (ioFlags == 0)
        {
            if (_lastAttempt.Count >= Capacity && !_lastAttempt.ContainsKey(key))
            {
                _lastAttempt.Clear();
            }

            _lastAttempt[key] = write;
            return false;
        }

        return _lastAttempt.Remove(key, out Attempt remembered) && remembered == write;
    }

    /// <summary>Forgets the attempts of a process's threads, so a reused pid starts clean.</summary>
    /// <param name="processId">The process that ended.</param>
    public void ProcessEnded(int processId)
    {
        foreach ((int ProcessId, int ThreadId) key in _lastAttempt.Keys)
        {
            if (key.ProcessId == processId)
            {
                _lastAttempt.Remove(key);
            }
        }
    }

    private readonly record struct Attempt(ulong FileObject, long Offset, int IoSize);
}
