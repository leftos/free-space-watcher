using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Writes;

namespace FreeSpaceWatcher.Service.Alerts;

/// <summary>One file an alert's writer grew: its size before the growth and the growth itself.</summary>
/// <param name="ProcessName">The writer's name.</param>
/// <param name="Path">The file's full path.</param>
/// <param name="SizeBefore">The file's size before the growth, never below 0.</param>
/// <param name="Growth">The file's end-of-file growth within the write window.</param>
internal sealed record TrackedFile(string ProcessName, string Path, long SizeBefore, long Growth);

/// <summary>What re-measuring an alert's tracked files found.</summary>
/// <param name="Cleared">Whether the remaining growth is 10 % of the original growth or less.</param>
/// <param name="RemovedBytes">The original growth minus the remaining growth, never below 0.</param>
/// <param name="Removers">The writers whose files lost growth, most bytes first.</param>
internal sealed record GrowthCheck(bool Cleared, long RemovedBytes, IReadOnlyList<string> Removers);

/// <summary>The file growth an alert's top writers caused, re-measured to tell whether they removed it again.</summary>
/// <remarks>
/// A file's remaining growth is its current size minus its size before the growth, never below 0, and 0 when the file is gone.
/// The growth counts as removed when the sum of the remaining growth is at most 10 % of the sum of the original growth.
/// </remarks>
internal sealed class TrackedGrowth
{
    private readonly TrackedFile[] _files;
    private readonly long _totalGrowth;

    private TrackedGrowth(TrackedFile[] files)
    {
        _files = files;
        _totalGrowth = files.Sum(f => f.Growth);
    }

    /// <summary>Collects the growth of the alert's listed files whose growth is positive and whose size was measured.</summary>
    /// <param name="alert">The alert, with its files' current sizes.</param>
    /// <returns>The tracked growth, or null when no listed file qualifies.</returns>
    public static TrackedGrowth? From(Alert alert)
    {
        TrackedFile[] files = [.. alert.Processes.SelectMany(p => p.Files.Select(f => Track(p, f)).OfType<TrackedFile>())];
        return files.Length == 0 ? null : new TrackedGrowth(files);
    }

    /// <summary>Re-measures every tracked file.</summary>
    /// <param name="remainingGrowth">Returns a tracked file's remaining growth.</param>
    /// <returns>Whether the growth was removed, how much was, and by whom.</returns>
    public GrowthCheck Check(Func<TrackedFile, long> remainingGrowth)
    {
        long remaining = 0;
        Dictionary<string, long> removedBy = new(StringComparer.OrdinalIgnoreCase);
        foreach (TrackedFile file in _files)
        {
            long left = remainingGrowth(file);
            remaining += left;
            if (file.Growth > left)
            {
                removedBy[file.ProcessName] = removedBy.GetValueOrDefault(file.ProcessName) + (file.Growth - left);
            }
        }

        string[] removers = [.. removedBy.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key)];
        return new GrowthCheck(remaining * 10 <= _totalGrowth, Math.Max(0, _totalGrowth - remaining), removers);
    }

    private static TrackedFile? Track(ProcessWriteReport process, FileWrite file) =>
        file.ExtendBytes > 0 && file.CurrentSize is long size
            ? new TrackedFile(process.Name, file.Path, Math.Max(0, size - file.ExtendBytes), file.ExtendBytes)
            : null;
}
