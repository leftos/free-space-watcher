namespace FreeSpaceWatcher.Core.Writes;

/// <summary>One process instance; a pid reused after the process ended is a different instance.</summary>
/// <remarks>A placeholder for a pid whose start was not seen has <paramref name="startSeen"/> false and its first event's time as start.</remarks>
internal sealed class TrackedProcess(int pid, string name, string? exePath, DateTimeOffset startTime, bool startSeen)
{
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TrackedFile> _folded = new(StringComparer.OrdinalIgnoreCase);

    public int Pid => pid;

    public string Name => name;

    public string? ExePath => exePath;

    public DateTimeOffset StartTime => startTime;

    public bool StartSeen => startSeen;

    /// <summary>Returns the entry a write to <paramref name="path"/> counts against, folding it once the file cap is reached.</summary>
    public TrackedFile FileFor(string path, char drive, int fileCap)
    {
        if (_files.TryGetValue(path, out TrackedFile? file))
        {
            return file;
        }

        if (_files.Count < fileCap)
        {
            file = new TrackedFile(this, drive, path, path, IsFolded: false);
            _files.Add(path, file);
            return file;
        }

        string folder = Path.GetDirectoryName(path) ?? path;
        if (!_folded.TryGetValue(folder, out file))
        {
            file = new TrackedFile(this, drive, Path.Join(folder, "*"), folder, IsFolded: true);
            _folded.Add(folder, file);
        }

        return file;
    }

    public void Forget(TrackedFile file) => (file.IsFolded ? _folded : _files).Remove(file.Key);
}

/// <summary>A file (or a folder's folded other files) one process wrote to, and how many buckets still mention it.</summary>
internal sealed record TrackedFile(TrackedProcess Owner, char Drive, string Path, string Key, bool IsFolded)
{
    public int BucketCount { get; set; }

    public bool Equals(TrackedFile? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>Activity totals for one file.</summary>
internal sealed class FileStats
{
    public long BytesWritten { get; private set; }

    public long ExtendBytes { get; private set; }

    public int Created { get; private set; }

    public int Deleted { get; private set; }

    public void Apply(WriteKind kind, long bytes)
    {
        switch (kind)
        {
            case WriteKind.Write:
                BytesWritten += bytes;
                break;
            case WriteKind.Extend:
                ExtendBytes += bytes;
                break;
            case WriteKind.Create:
                Created++;
                break;
            case WriteKind.Delete:
                Deleted++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown write kind.");
        }
    }

    public void Add(FileStats other)
    {
        BytesWritten += other.BytesWritten;
        ExtendBytes += other.ExtendBytes;
        Created += other.Created;
        Deleted += other.Deleted;
    }
}

/// <summary>One process's totals on one drive, gathered for a query.</summary>
internal sealed class ProcessTally(TrackedProcess process)
{
    public TrackedProcess Process => process;

    public Dictionary<TrackedFile, FileStats> Files { get; } = [];

    public long BytesWritten { get; private set; }

    public long ExtendBytes { get; private set; }

    public void Add(TrackedFile file, FileStats stats)
    {
        if (!Files.TryGetValue(file, out FileStats? total))
        {
            total = new FileStats();
            Files.Add(file, total);
        }

        total.Add(stats);
        BytesWritten += stats.BytesWritten;
        ExtendBytes += stats.ExtendBytes;
    }
}
