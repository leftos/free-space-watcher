using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Service.Sampling;

/// <summary>
/// Each watched drive's free-space history for drive history requests: a ring the sampler appends to every tick, kept for its
/// own length, independent of the rate and write windows.
/// </summary>
/// <remarks>Safe to call from any thread: the sampler appends while pipe connections read.</remarks>
public sealed class RecentSamples
{
    /// <summary>The shortest history kept, whatever the rate window.</summary>
    public static readonly TimeSpan MinimumKeep = TimeSpan.FromSeconds(900);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DriveSample>> _samples = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets how long the sampler keeps history for a rate window: the longer of 900 s and the rate window.</summary>
    /// <param name="rateWindow">The rate window.</param>
    /// <returns>How long to keep samples.</returns>
    public static TimeSpan KeepFor(TimeSpan rateWindow) => rateWindow > MinimumKeep ? rateWindow : MinimumKeep;

    /// <summary>Appends a drive's newest sample and drops its samples older than <paramref name="keep"/> before it.</summary>
    /// <param name="letter">The drive letter, e.g. "C".</param>
    /// <param name="sample">The newest sample.</param>
    /// <param name="keep">How far back to keep samples.</param>
    public void Add(string letter, DriveSample sample, TimeSpan keep)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            if (!_samples.TryGetValue(letter, out Queue<DriveSample>? ring))
            {
                ring = new Queue<DriveSample>();
                _samples.Add(letter, ring);
            }

            ring.Enqueue(sample);
            DateTimeOffset oldest = sample.Time - keep;
            while (ring.Peek().Time < oldest)
            {
                ring.Dequeue();
            }
        }
    }

    /// <summary>Forgets a drive's samples, because it went away.</summary>
    /// <param name="letter">The drive letter.</param>
    public void Remove(string letter)
    {
        lock (_gate)
        {
            _samples.Remove(letter);
        }
    }

    /// <summary>Forgets the samples of every drive not in <paramref name="letters"/>, because it is no longer watched.</summary>
    /// <param name="letters">The drives whose samples to keep.</param>
    public void RetainOnly(IReadOnlyCollection<string> letters)
    {
        ArgumentNullException.ThrowIfNull(letters);
        lock (_gate)
        {
            foreach (string letter in _samples.Keys.Where(k => !letters.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                _samples.Remove(letter);
            }
        }
    }

    /// <summary>Gets a drive's samples no older than <paramref name="span"/> before its newest one.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <param name="span">How far back to go; zero or less returns nothing.</param>
    /// <returns>The samples, oldest first; empty for a drive with none.</returns>
    public IReadOnlyList<DriveSample> Get(string letter, TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
        {
            return [];
        }

        lock (_gate)
        {
            if (!_samples.TryGetValue(letter, out Queue<DriveSample>? ring) || ring.Count == 0)
            {
                return [];
            }

            DateTimeOffset oldest = ring.Last().Time - span;
            return [.. ring.Where(s => s.Time >= oldest)];
        }
    }
}
