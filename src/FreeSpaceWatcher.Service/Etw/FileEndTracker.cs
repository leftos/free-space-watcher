namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Tracks each file's known end so a write or an end-of-file set can be turned into the growth it caused.</summary>
/// <remarks>
/// Paths compare without regard to case. At most <c>capacity</c> paths are tracked; adding one more evicts the path used
/// least recently. When a path's end is unknown, its first growth is reported as 0 and counted in
/// <see cref="GrowthsWithoutBase"/>, and later growth counts from the end that was seen.
/// </remarks>
/// <param name="capacity">The most paths tracked; must be at least 1.</param>
internal sealed class FileEndTracker(int capacity)
{
    /// <summary>The number of paths the router tracks an end for.</summary>
    public const int DefaultCapacity = 16_384;

    private readonly int _capacity =
        capacity >= 1 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be at least 1.");
    private readonly Dictionary<string, LinkedListNode<KnownEnd>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<KnownEnd> _byRecency = [];
    private long _withoutBase;

    /// <summary>Gets how many growths were reported as 0 because the file's earlier end was unknown.</summary>
    public long GrowthsWithoutBase => Interlocked.Read(ref _withoutBase);

    /// <summary>Sets a path's known end, as after a creating or truncating open.</summary>
    /// <param name="path">The file's DOS path.</param>
    /// <param name="size">The file's size.</param>
    public void Reset(string path, long size) => Store(path, size);

    /// <summary>Returns a path's known end without changing how recently it was used.</summary>
    /// <param name="path">The file's DOS path.</param>
    /// <returns>The known end, or null when the path is not tracked.</returns>
    public long? EndOf(string path) => TryGetKnown(path, out long known) ? known : null;

    /// <summary>Stops tracking a path, as after its deletion.</summary>
    /// <param name="path">The file's DOS path.</param>
    public void Forget(string path)
    {
        if (_byPath.Remove(path, out LinkedListNode<KnownEnd>? node))
        {
            _byRecency.Remove(node);
        }
    }

    /// <summary>Moves a path's known end forward to the end of a write.</summary>
    /// <param name="path">The file's DOS path.</param>
    /// <param name="end">The byte just past the written extent.</param>
    /// <returns>The bytes the write extended the file by; 0 for an overwrite or when the earlier end is unknown.</returns>
    public long Advance(string path, long end)
    {
        if (!TryGetKnown(path, out long known))
        {
            Interlocked.Increment(ref _withoutBase);
            Store(path, end);
            return 0;
        }

        Store(path, Math.Max(known, end));
        return Math.Max(0, end - known);
    }

    /// <summary>Sets a path's known end to an explicit new size, so writes after a truncation count as growth again.</summary>
    /// <param name="path">The file's DOS path.</param>
    /// <param name="size">The file's new size.</param>
    /// <returns>The bytes the file grew by; 0 for a shrink or when the earlier end is unknown.</returns>
    public long Set(string path, long size)
    {
        if (!TryGetKnown(path, out long known))
        {
            Interlocked.Increment(ref _withoutBase);
            Store(path, size);
            return 0;
        }

        Store(path, size);
        return Math.Max(0, size - known);
    }

    private bool TryGetKnown(string path, out long known)
    {
        if (_byPath.TryGetValue(path, out LinkedListNode<KnownEnd>? node))
        {
            known = node.Value.End;
            return true;
        }

        known = 0;
        return false;
    }

    private void Store(string path, long end)
    {
        if (_byPath.Remove(path, out LinkedListNode<KnownEnd>? node))
        {
            _byRecency.Remove(node);
        }
        else if (_byPath.Count >= _capacity && _byRecency.First is { } oldest)
        {
            _byRecency.Remove(oldest);
            _byPath.Remove(oldest.Value.Path);
        }

        _byPath[path] = _byRecency.AddLast(new KnownEnd(path, end));
    }

    private sealed record KnownEnd(string Path, long End);
}
