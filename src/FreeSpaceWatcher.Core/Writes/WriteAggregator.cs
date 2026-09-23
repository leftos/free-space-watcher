using FreeSpaceWatcher.Core.Triggers;
using static System.FormattableString;

namespace FreeSpaceWatcher.Core.Writes;

/// <summary>
/// Keeps per-second write totals over a rolling window, keyed by drive, process and file, and answers who wrote what.
/// Thread-safe: one thread records while another queries.
/// </summary>
/// <param name="window">How far back the totals reach from the newest event or query time.</param>
/// <param name="perProcessFileCap">Distinct files tracked per process before further files fold into "folder\*".</param>
public sealed class WriteAggregator(TimeSpan window, int perProcessFileCap)
{
    private readonly Lock _gate = new();
    private readonly long _windowSeconds = Math.Max(1, (long)Math.Ceiling(window.TotalSeconds));
    private readonly Dictionary<int, TrackedProcess> _live = [];
    private readonly SortedDictionary<long, Dictionary<TrackedFile, FileStats>> _buckets = [];
    private long _horizonSecond = long.MinValue;

    /// <summary>Records a process start, so its writes resolve to its name even after it exits.</summary>
    /// <param name="pid">The process id.</param>
    /// <param name="name">The process name.</param>
    /// <param name="exePath">The executable path, when known.</param>
    /// <param name="time">When the process started; with the pid, it identifies the process instance.</param>
    public void ProcessStarted(int pid, string name, string? exePath, DateTimeOffset time)
    {
        lock (_gate)
        {
            _live[pid] = new TrackedProcess(pid, name, exePath, time, startSeen: true);
        }
    }

    /// <summary>Records a process exit; its writes stay until they leave the window, and a later event for the pid is a new process.</summary>
    /// <param name="pid">The process id.</param>
    /// <param name="time">When the process ended; an end older than the current instance's start is ignored.</param>
    public void ProcessEnded(int pid, DateTimeOffset time)
    {
        lock (_gate)
        {
            if (_live.TryGetValue(pid, out TrackedProcess? process) && process.StartTime <= time)
            {
                _live.Remove(pid);
            }
        }
    }

    /// <summary>Adds one file operation; operations older than the window are dropped.</summary>
    /// <param name="writeEvent">The operation.</param>
    public void Record(WriteEvent writeEvent)
    {
        ArgumentNullException.ThrowIfNull(writeEvent);
        long second = writeEvent.Time.ToUnixTimeSeconds();
        lock (_gate)
        {
            AdvanceTo(second);
            if (second <= _horizonSecond - _windowSeconds)
            {
                return;
            }

            TrackedProcess process = ResolveProcess(writeEvent.ProcessId, writeEvent.Time);
            TrackedFile file = process.FileFor(writeEvent.Path, char.ToUpperInvariant(writeEvent.Drive), perProcessFileCap);
            if (!_buckets.TryGetValue(second, out Dictionary<TrackedFile, FileStats>? bucket))
            {
                bucket = [];
                _buckets.Add(second, bucket);
            }

            if (!bucket.TryGetValue(file, out FileStats? stats))
            {
                stats = new FileStats();
                bucket.Add(file, stats);
                file.BucketCount++;
            }

            stats.Apply(writeEvent.Kind, writeEvent.Bytes);
        }
    }

    /// <summary>Drops the totals that fall outside the window ending at <paramref name="now"/>.</summary>
    /// <param name="now">The end of the window.</param>
    public void Evict(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceTo(now.ToUnixTimeSeconds());
        }
    }

    /// <summary>Reports what was written to a drive within the window ending at <paramref name="now"/>.</summary>
    /// <param name="drive">The drive letter.</param>
    /// <param name="now">The end of the window.</param>
    /// <param name="topProcesses">How many processes to list.</param>
    /// <param name="topFolders">How many folders to list per process.</param>
    /// <param name="topFiles">How many files to list per process.</param>
    /// <returns>The drive's totals and its top writers.</returns>
    public DriveWriteSnapshot Snapshot(char drive, DateTimeOffset now, int topProcesses, int topFolders, int topFiles)
    {
        lock (_gate)
        {
            AdvanceTo(now.ToUnixTimeSeconds());
            List<ProcessTally> tallies = Collect(char.ToUpperInvariant(drive));
            ProcessWriteReport[] reports = [.. tallies.Take(topProcesses).Select(t => BuildReport(t, topFolders, topFiles))];
            return new DriveWriteSnapshot(tallies.Sum(t => t.BytesWritten), tallies.Sum(t => t.ExtendBytes), reports);
        }
    }

    /// <summary>Returns every process's bytes written to a drive within the window ending at <paramref name="now"/>.</summary>
    /// <param name="drive">The drive letter.</param>
    /// <param name="now">The end of the window.</param>
    /// <returns>The totals, by bytes descending then process id ascending.</returns>
    public IReadOnlyList<ProcessWriteTotal> Totals(char drive, DateTimeOffset now)
    {
        lock (_gate)
        {
            AdvanceTo(now.ToUnixTimeSeconds());
            return [.. Collect(char.ToUpperInvariant(drive)).Select(t => new ProcessWriteTotal(t.Process.Pid, t.Process.Name, t.BytesWritten))];
        }
    }

    private TrackedProcess ResolveProcess(int pid, DateTimeOffset time)
    {
        if (!_live.TryGetValue(pid, out TrackedProcess? process))
        {
            process = new TrackedProcess(pid, Invariant($"pid {pid}"), null, time, startSeen: false);
            _live.Add(pid, process);
        }

        return process;
    }

    private void AdvanceTo(long second)
    {
        if (second <= _horizonSecond)
        {
            return;
        }

        _horizonSecond = second;
        long cutoff = second - _windowSeconds;
        while (_buckets.Count > 0)
        {
            KeyValuePair<long, Dictionary<TrackedFile, FileStats>> oldest = _buckets.First();
            if (oldest.Key > cutoff)
            {
                break;
            }

            foreach (TrackedFile file in oldest.Value.Keys)
            {
                file.BucketCount--;
                if (file.BucketCount == 0)
                {
                    file.Owner.Forget(file);
                }
            }

            _buckets.Remove(oldest.Key);
        }
    }

    private List<ProcessTally> Collect(char drive)
    {
        Dictionary<TrackedProcess, ProcessTally> tallies = [];
        foreach (Dictionary<TrackedFile, FileStats> bucket in _buckets.Values)
        {
            foreach (KeyValuePair<TrackedFile, FileStats> entry in bucket)
            {
                if (entry.Key.Drive != drive)
                {
                    continue;
                }

                if (!tallies.TryGetValue(entry.Key.Owner, out ProcessTally? tally))
                {
                    tally = new ProcessTally(entry.Key.Owner);
                    tallies.Add(entry.Key.Owner, tally);
                }

                tally.Add(entry.Key, entry.Value);
            }
        }

        return [.. tallies.Values.OrderByDescending(t => t.BytesWritten).ThenBy(t => t.Process.Pid).ThenBy(t => t.Process.StartTime)];
    }

    private static ProcessWriteReport BuildReport(ProcessTally tally, int topFolders, int topFiles)
    {
        FolderWrite[] folders =
        [
            .. tally
                .Files.GroupBy(f => Path.GetDirectoryName(f.Key.Path) ?? f.Key.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => new FolderWrite(g.Key, g.Sum(f => f.Value.BytesWritten)))
                .OrderByDescending(f => f.Bytes)
                .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(topFolders),
        ];
        FileWrite[] files =
        [
            .. tally
                .Files.OrderByDescending(f => f.Value.BytesWritten)
                .ThenByDescending(f => f.Value.ExtendBytes)
                .ThenBy(f => f.Key.Path, StringComparer.OrdinalIgnoreCase)
                .Take(topFiles)
                .Select(f => new FileWrite
                {
                    Path = f.Key.Path,
                    BytesWritten = f.Value.BytesWritten,
                    ExtendBytes = f.Value.ExtendBytes,
                    Created = f.Value.Created > 0,
                    Deleted = f.Value.Deleted > 0,
                }),
        ];
        return new ProcessWriteReport
        {
            ProcessId = tally.Process.Pid,
            Name = tally.Process.Name,
            ExePath = tally.Process.ExePath,
            StartTime = tally.Process.StartSeen ? tally.Process.StartTime : null,
            BytesWritten = tally.BytesWritten,
            ExtendBytes = tally.ExtendBytes,
            FilesCreated = tally.Files.Values.Sum(s => s.Created),
            FilesDeleted = tally.Files.Values.Sum(s => s.Deleted),
            Folders = folders,
            Files = files,
        };
    }
}
