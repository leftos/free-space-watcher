using FreeSpaceWatcher.Core.Triggers;

namespace FreeSpaceWatcher.Service.Sampling;

/// <summary>One drive's recent samples, kept for the length of the write window.</summary>
internal sealed class SampleWindow
{
    private readonly Queue<DriveSample> _samples = new();

    /// <summary>Gets a copy of the samples, oldest first.</summary>
    public IReadOnlyList<DriveSample> Samples => [.. _samples];

    /// <summary>Adds a sample and drops the ones older than <paramref name="window"/> before it.</summary>
    /// <param name="sample">The newest sample.</param>
    /// <param name="window">How far back to keep samples.</param>
    public void Add(DriveSample sample, TimeSpan window)
    {
        _samples.Enqueue(sample);
        DateTimeOffset oldest = sample.Time - window;
        while (_samples.Peek().Time < oldest)
        {
            _samples.Dequeue();
        }
    }

    /// <summary>Forgets every sample, because the drive went away.</summary>
    public void Clear() => _samples.Clear();
}
