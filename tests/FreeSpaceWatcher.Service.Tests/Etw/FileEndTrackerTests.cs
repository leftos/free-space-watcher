using FreeSpaceWatcher.Service.Etw;

namespace FreeSpaceWatcher.Service.Tests.Etw;

public sealed class FileEndTrackerTests
{
    private const string FilePath = @"C:\data\growing.bin";
    private const int Chunk = 1 << 20;

    [Fact]
    public void Advance_SequentialWritesAfterCreate_GrowthEqualsBytesWritten()
    {
        FileEndTracker tracker = new(FileEndTracker.DefaultCapacity);
        tracker.Reset(FilePath, 0);

        long growth = 0;
        for (int i = 0; i < 8; i++)
        {
            growth += tracker.Advance(FilePath, (long)(i + 1) * Chunk);
        }

        Assert.Equal(8L * Chunk, growth);
        Assert.Equal(0, tracker.GrowthsWithoutBase);
    }

    [Fact]
    public void Advance_RepeatedEndAndOverwrite_AreNoGrowth()
    {
        FileEndTracker tracker = new(FileEndTracker.DefaultCapacity);
        tracker.Reset(FilePath, 0);
        tracker.Advance(FilePath, 4L * Chunk);

        Assert.Equal(0, tracker.Advance(FilePath, 4L * Chunk));
        Assert.Equal(0, tracker.Advance(FilePath, 2L * Chunk));
        Assert.Equal(Chunk, tracker.Advance(FilePath.ToUpperInvariant(), 5L * Chunk));
    }

    [Fact]
    public void Advance_UnknownBase_IsZeroThenCountsFromThere()
    {
        FileEndTracker tracker = new(FileEndTracker.DefaultCapacity);

        Assert.Equal(0, tracker.Advance(FilePath, 10L * Chunk));
        Assert.Equal(1, tracker.GrowthsWithoutBase);
        Assert.Equal(Chunk, tracker.Advance(FilePath, 11L * Chunk));
        Assert.Equal(1, tracker.GrowthsWithoutBase);
    }

    [Fact]
    public void Set_TruncateThenWrite_CountsGrowthAgain()
    {
        FileEndTracker tracker = new(FileEndTracker.DefaultCapacity);
        tracker.Reset(FilePath, 0);
        tracker.Advance(FilePath, 4L * Chunk);

        Assert.Equal(0, tracker.Set(FilePath, Chunk));
        Assert.Equal(Chunk, tracker.Advance(FilePath, 2L * Chunk));
        Assert.Equal(3L * Chunk, tracker.Set(FilePath, 5L * Chunk));
    }
}
