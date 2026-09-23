using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;

namespace FreeSpaceWatcher.Core.Tests.Writes;

public sealed class WriteAggregatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    [Fact]
    public void EventsOutsideWindow_AreEvicted()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.ProcessStarted(1, "one", null, T0);
        aggregator.Record(Write(0, 1, @"C:\a\old.log", 100));
        aggregator.Record(Write(40, 1, @"C:\a\new.log", 50));
        aggregator.Record(Write(90, 2, @"C:\b\x.log", 10));

        DriveWriteSnapshot snapshot = Snap(aggregator, 'C', 90);
        Assert.Equal(60, snapshot.TotalBytesWritten);
        ProcessWriteReport one = Assert.Single(snapshot.Processes, p => p.ProcessId == 1);
        Assert.Equal(50, one.BytesWritten);
        Assert.Equal(@"C:\a\new.log", Assert.Single(one.Files).Path);

        aggregator.Record(Write(10, 1, @"C:\a\late.log", 1000));
        Assert.Equal(60, Snap(aggregator, 'C', 90).TotalBytesWritten);

        DriveWriteSnapshot later = Snap(aggregator, 'C', 200);
        Assert.Empty(later.Processes);
        Assert.Equal(0, later.TotalBytesWritten);
    }

    [Fact]
    public void FileCap_FoldsIntoOtherFiles()
    {
        WriteAggregator aggregator = new(Window, 2);
        aggregator.ProcessStarted(1, "writer", @"C:\tools\writer.exe", T0);
        string[] names = ["a", "b", "c", "d"];
        foreach (string name in names)
        {
            aggregator.Record(Write(1, 1, $@"C:\logs\{name}.log", 10));
        }

        aggregator.Record(Write(2, 1, @"C:\logs\a.log", 5));

        ProcessWriteReport report = Assert.Single(Snap(aggregator, 'C', 2).Processes);
        Assert.Equal(T0, report.StartTime);
        Assert.Equal(45, report.BytesWritten);
        (string, long)[] expected = [(@"C:\logs\*", 20), (@"C:\logs\a.log", 15), (@"C:\logs\b.log", 10)];
        Assert.Equal(expected, report.Files.Select(f => (f.Path, f.BytesWritten)));
    }

    [Fact]
    public void Folders_RollUpToParent()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Write(1, 1, @"C:\x\1.txt", 10));
        aggregator.Record(Write(1, 1, @"C:\x\2.txt", 20));
        aggregator.Record(Write(1, 1, @"C:\y\sub\3.txt", 5));

        ProcessWriteReport report = Assert.Single(Snap(aggregator, 'C', 1).Processes);

        FolderWrite[] expected = [new(@"C:\x", 30), new(@"C:\y\sub", 5)];
        Assert.Equal(expected, report.Folders);
    }

    [Fact]
    public void TopN_OrdersByBytesThenPid()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Write(1, 3, @"C:\p3.log", 100));
        aggregator.Record(Write(1, 1, @"C:\p1.log", 100));
        aggregator.Record(Write(1, 2, @"C:\p2.log", 200));
        aggregator.Record(Write(1, 4, @"C:\p4.log", 50));

        DriveWriteSnapshot snapshot = aggregator.Snapshot('C', T0.AddSeconds(1), 3, 10, 10);

        int[] expected = [2, 1, 3];
        Assert.Equal(expected, snapshot.Processes.Select(p => p.ProcessId));
        Assert.Equal(450, snapshot.TotalBytesWritten);
    }

    [Fact]
    public void PidReuse_AfterEnd_IsNewProcess()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.ProcessStarted(100, "first", null, T0);
        aggregator.Record(Write(1, 100, @"C:\a.log", 10));
        aggregator.ProcessEnded(100, T0.AddSeconds(2));
        aggregator.ProcessStarted(100, "second", null, T0.AddSeconds(3));
        aggregator.Record(Write(4, 100, @"C:\b.log", 20));

        DriveWriteSnapshot snapshot = Snap(aggregator, 'C', 4);

        string[] expected = ["second", "first"];
        Assert.Equal(expected, snapshot.Processes.Select(p => p.Name));
        Assert.All(snapshot.Processes, p => Assert.Equal(100, p.ProcessId));
        Assert.Equal(2, aggregator.Totals('C', T0.AddSeconds(4)).Count);
    }

    [Fact]
    public void UnknownPid_GetsPlaceholderName()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Write(1, 42, @"C:\x.log", 10));

        ProcessWriteReport report = Assert.Single(Snap(aggregator, 'C', 1).Processes);

        Assert.Equal("pid 42", report.Name);
        Assert.Null(report.ExePath);
        Assert.Null(report.StartTime);
    }

    [Fact]
    public void DrivesAreSeparate()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Write(1, 1, @"C:\c.log", 10));
        aggregator.Record(Event(1, 1, @"D:\d.log", WriteKind.Extend, 20));
        aggregator.Record(Event(1, 2, @"D:\e.log", WriteKind.Extend, 5));

        DriveWriteSnapshot c = Snap(aggregator, 'C', 1);
        IReadOnlyList<ProcessWriteTotal> d = aggregator.Totals('d', T0.AddSeconds(1));

        Assert.Equal(10, c.TotalBytesWritten);
        Assert.Equal(1, Assert.Single(c.Processes).ProcessId);
        ProcessWriteTotal[] expected = [new(1, "pid 1", 20), new(2, "pid 2", 5)];
        Assert.Equal(expected, d);
    }

    [Fact]
    public void CreateDeleteExtend_Counted()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Event(1, 1, @"C:\d\a.log", WriteKind.Create, 0));
        aggregator.Record(Event(1, 1, @"C:\d\a.log", WriteKind.Write, 10));
        aggregator.Record(Event(2, 1, @"C:\d\a.log", WriteKind.Extend, 100));
        aggregator.Record(Event(2, 1, @"C:\d\b.log", WriteKind.Delete, 0));
        aggregator.Record(Event(3, 1, @"C:\d\c.log", WriteKind.Create, 0));

        DriveWriteSnapshot snapshot = Snap(aggregator, 'C', 3);
        ProcessWriteReport report = Assert.Single(snapshot.Processes);

        Assert.Equal(2, report.FilesCreated);
        Assert.Equal(1, report.FilesDeleted);
        Assert.Equal(10, report.BytesWritten);
        Assert.Equal(100, report.ExtendBytes);
        Assert.Equal(100, snapshot.TotalExtendBytes);
        FileWrite a = Assert.Single(report.Files, f => f.Path == @"C:\d\a.log");
        Assert.True(a.Created);
        Assert.False(a.Deleted);
        Assert.Equal(100, a.ExtendBytes);
        Assert.Null(a.CurrentSize);
        FileWrite b = Assert.Single(report.Files, f => f.Path == @"C:\d\b.log");
        Assert.True(b.Deleted);
        Assert.False(b.Created);
    }

    [Fact]
    public void DeleteAndShrink_CountedAsRemoved()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Event(1, 1, @"C:\d\a.log", WriteKind.Extend, 100));
        aggregator.Record(Event(2, 1, @"C:\d\a.log", WriteKind.Shrink, 20));
        aggregator.Record(Event(2, 1, @"C:\d\b.log", WriteKind.Delete, 30));
        aggregator.Record(Event(2, 2, @"C:\d\c.log", WriteKind.Delete, 7));

        DriveWriteSnapshot snapshot = Snap(aggregator, 'C', 2);
        ProcessWriteReport one = Assert.Single(snapshot.Processes, p => p.ProcessId == 1);
        ProcessWriteReport two = Assert.Single(snapshot.Processes, p => p.ProcessId == 2);

        Assert.Equal(50, one.RemovedBytes);
        Assert.Equal(100, one.ExtendBytes);
        Assert.Equal(1, one.FilesDeleted);
        Assert.Equal(7, two.RemovedBytes);
        ProcessWriteTotal[] expected = [new(1, "pid 1", 50), new(2, "pid 2", 0)];
        Assert.Equal(expected, aggregator.Totals('C', T0.AddSeconds(2)));
    }

    [Fact]
    public void NetGrowth_FlooredAtZero()
    {
        WriteAggregator aggregator = new(Window, 100);
        aggregator.Record(Event(1, 1, @"C:\d\a.log", WriteKind.Extend, 10));
        aggregator.Record(Event(1, 1, @"C:\d\old.log", WriteKind.Delete, 50));

        ProcessWriteTotal total = Assert.Single(aggregator.Totals('C', T0.AddSeconds(1)));

        Assert.Equal(0, total.NetGrowthBytes);
        Assert.Equal(50, Assert.Single(Snap(aggregator, 'C', 1).Processes).RemovedBytes);
    }

    private static WriteEvent Event(int second, int pid, string path, WriteKind kind, long bytes) =>
        new()
        {
            Time = T0.AddSeconds(second),
            ProcessId = pid,
            Drive = path[0],
            Path = path,
            Kind = kind,
            Bytes = bytes,
        };

    private static WriteEvent Write(int second, int pid, string path, long bytes) => Event(second, pid, path, WriteKind.Write, bytes);

    private static DriveWriteSnapshot Snap(WriteAggregator aggregator, char drive, int second) =>
        aggregator.Snapshot(drive, T0.AddSeconds(second), 10, 10, 10);
}
