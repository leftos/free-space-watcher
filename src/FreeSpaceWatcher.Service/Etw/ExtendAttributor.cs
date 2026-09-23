namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Decides which process an end-of-file growth is credited to.</summary>
/// <remarks>
/// The cache manager raises the end-of-file events of a cached, growing file on System's threads, so an extend reported by
/// System is credited to the process that last wrote the same path through a caller's (non-paging) write. The last-writer
/// map holds at most <c>capacity</c> paths, evicting the least recently written one, and forgets a process when it ends.
/// </remarks>
/// <param name="capacity">The most paths remembered; must be at least 1.</param>
internal sealed class ExtendAttributor(int capacity)
{
    /// <summary>The pid of the System process, which raises the cache manager's end-of-file events.</summary>
    public const int SystemProcessId = 4;

    /// <summary>The number of paths the router remembers a last writer for.</summary>
    public const int DefaultCapacity = 16_384;

    private readonly int _capacity =
        capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be at least 1.");
    private readonly Dictionary<string, LinkedListNode<LastWrite>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<LastWrite> _byRecency = [];
    private readonly Dictionary<int, int> _pathCountByProcess = [];

    /// <summary>Gets how many paths currently have a known last writer.</summary>
    public int Count => _byPath.Count;

    /// <summary>Remembers a process as the last writer of a path.</summary>
    /// <param name="processId">The writing process.</param>
    /// <param name="path">The written file's DOS path.</param>
    public void Wrote(int processId, string path)
    {
        if (_byPath.TryGetValue(path, out LinkedListNode<LastWrite>? node))
        {
            Remove(node);
        }
        else if (_byPath.Count >= _capacity && _byRecency.First is { } oldest)
        {
            Remove(oldest);
        }

        LinkedListNode<LastWrite> added = _byRecency.AddLast(new LastWrite(processId, path));
        _byPath[path] = added;
        _pathCountByProcess[processId] = _pathCountByProcess.GetValueOrDefault(processId) + 1;
    }

    /// <summary>Gets the process an end-of-file growth is credited to.</summary>
    /// <param name="processId">The process the event was raised in.</param>
    /// <param name="path">The extended file's DOS path.</param>
    /// <returns>The last writer of the path when the event came from System and a writer is known; otherwise <paramref name="processId"/>.</returns>
    public int CreditExtend(int processId, string path) =>
        processId == SystemProcessId && _byPath.TryGetValue(path, out LinkedListNode<LastWrite>? node) ? node.Value.ProcessId : processId;

    /// <summary>Forgets every path a process was the last writer of.</summary>
    /// <param name="processId">The process that ended.</param>
    public void ProcessEnded(int processId)
    {
        if (!_pathCountByProcess.ContainsKey(processId))
        {
            return;
        }

        LinkedListNode<LastWrite>? node = _byRecency.First;
        while (node is not null)
        {
            LinkedListNode<LastWrite>? next = node.Next;
            if (node.Value.ProcessId == processId)
            {
                Remove(node);
            }

            node = next;
        }
    }

    private void Remove(LinkedListNode<LastWrite> node)
    {
        _byRecency.Remove(node);
        _byPath.Remove(node.Value.Path);
        int remaining = _pathCountByProcess[node.Value.ProcessId] - 1;
        if (remaining == 0)
        {
            _pathCountByProcess.Remove(node.Value.ProcessId);
        }
        else
        {
            _pathCountByProcess[node.Value.ProcessId] = remaining;
        }
    }

    private sealed record LastWrite(int ProcessId, string Path);
}
