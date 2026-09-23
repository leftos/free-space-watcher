using System.Globalization;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Alerts;

namespace FreeSpaceWatcher.Tray.Tests.Alerts;

public sealed class AlertNodesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);

    [Fact]
    public void ListItem_ShowsDriveTriggerAndTimes()
    {
        AlertSummary summary = new()
        {
            Id = "a",
            Time = T0,
            Drive = "C",
            Trigger = TriggerKind.TimeToFull,
            Reason = "C: full in ~6 min",
            Acknowledged = false,
        };

        AlertListItem item = new(summary, T0.AddMinutes(2), TimeZoneInfo.Utc);

        Assert.Equal("C:", item.DriveLabel);
        Assert.Equal("Time to full", item.TriggerName);
        Assert.Equal("C: full in ~6 min", item.Reason);
        Assert.Equal("2 min ago", item.RelativeTime);
        Assert.False(string.IsNullOrEmpty(item.AbsoluteTime));
        Assert.True(item.IsUnacknowledged);
    }

    [Fact]
    public void ListItem_ResolvedAt_IsResolved_OtherwiseNot()
    {
        AlertSummary summary = new()
        {
            Id = "a",
            Time = T0,
            Drive = "C",
            Trigger = TriggerKind.DropRate,
            Reason = "C: losing 2.1 GB/min",
            Acknowledged = true,
        };

        Assert.False(new AlertListItem(summary, T0, TimeZoneInfo.Utc).IsResolved);
        Assert.True(new AlertListItem(summary with { ResolvedAt = T0.AddMinutes(1) }, T0, TimeZoneInfo.Utc).IsResolved);
    }

    [Fact]
    public void Details_ResolvedAlert_StripSaysHowLongAgoAndWhy_WithTheAbsoluteTimeForItsTooltip()
    {
        DateTimeOffset resolvedAt = T0.AddMinutes(3);
        Alert alert = Alert([]) with { ResolvedAt = resolvedAt, ResolvedReason = "pwsh deleted 14.0 GB it had written" };

        var details = AlertDetails.From(alert, T0.AddMinutes(5), TimeZoneInfo.Utc);

        Assert.True(details.IsResolved);
        Assert.Equal("Resolved 2 min ago: pwsh deleted 14.0 GB it had written", details.ResolvedText);
        Assert.Equal(AlertFormat.AbsoluteTime(resolvedAt, TimeZoneInfo.Utc, CultureInfo.CurrentCulture), details.ResolvedTime);
    }

    [Fact]
    public void Details_ResolvedWithoutAReason_StripSaysOnlyWhen()
    {
        Alert alert = Alert([]) with { ResolvedAt = T0.AddMinutes(3) };

        Assert.Equal("Resolved just now", AlertDetails.From(alert, T0.AddMinutes(3), TimeZoneInfo.Utc).ResolvedText);
    }

    [Fact]
    public void Details_UnresolvedAlert_HasNoResolvedStrip()
    {
        var details = AlertDetails.From(Alert([]), T0.AddMinutes(5), TimeZoneInfo.Utc);

        Assert.False(details.IsResolved);
        Assert.Null(details.ResolvedText);
        Assert.Null(details.ResolvedTime);
    }

    [Fact]
    public void WriteSummary_RemovedBytes_AreListedLast_AndLeftOutWhenZero()
    {
        ProcessWriteReport cleaned = Writer(1, 14L << 30) with { ExtendBytes = 14L << 30, RemovedBytes = 14L << 30 };
        ProcessWriteReport kept = Writer(2, 14L << 30) with { ExtendBytes = 14L << 30 };

        Assert.Equal("14.0 GB written · 14.0 GB growth · 14.0 GB removed", ProcessNode.From(cleaned, 14L << 30, isTopWriter: true).WriteSummary);
        Assert.Equal("14.0 GB written · 14.0 GB growth", ProcessNode.From(kept, 14L << 30, isTopWriter: true).WriteSummary);
    }

    [Fact]
    public void Details_HeaderValues_AndWritersRelativeToTheTopWriter()
    {
        Alert alert = Alert([Writer(1, 8L << 30), Writer(2, 2L << 30), Writer(3, 0)]);

        var details = AlertDetails.From(alert, T0, TimeZoneInfo.Utc);

        Assert.Equal("C:", details.Drive);
        Assert.Equal("Drop rate", details.TriggerName);
        Assert.False(string.IsNullOrEmpty(details.Time));
        Assert.Equal([1.0, 0.25, 0.0], details.Processes.Select(p => p.BarFraction));
    }

    [Fact]
    public void Details_OnlyTheTopWritersGroupsStartExpanded()
    {
        Alert alert = Alert([Writer(1, 8L << 30), Writer(2, 2L << 30)]);

        var details = AlertDetails.From(alert, T0, TimeZoneInfo.Utc);

        Assert.All(details.Processes[0].Children, g => Assert.True(g.IsExpanded));
        Assert.All(details.Processes[1].Children, g => Assert.False(g.IsExpanded));
    }

    [Fact]
    public void Details_NoWriters_HasNoCards()
    {
        var details = AlertDetails.From(Alert([]), T0, TimeZoneInfo.Utc);

        Assert.Empty(details.Processes);
    }

    [Fact]
    public void Details_OneWriter_HidesTheWriterBar()
    {
        var details = AlertDetails.From(Alert([Writer(1, 8L << 30)]), T0, TimeZoneInfo.Utc);

        Assert.All(details.Processes, p => Assert.False(p.ShowWriterBar));
    }

    [Fact]
    public void Details_TwoWriters_ShowTheWriterBar()
    {
        var details = AlertDetails.From(Alert([Writer(1, 8L << 30), Writer(2, 2L << 30)]), T0, TimeZoneInfo.Utc);

        Assert.All(details.Processes, p => Assert.True(p.ShowWriterBar));
    }

    [Fact]
    public void Writer_GroupHeadersCountTheirRows()
    {
        ProcessWriteReport report = Writer(1, 1L << 30) with
        {
            Folders = [new FolderWrite(@"C:\a", 1), new FolderWrite(@"C:\b", 2)],
            Files = [File(@"C:\a\x.bin", null)],
        };

        var node = ProcessNode.From(report, report.BytesWritten, isTopWriter: true);

        Assert.Equal(["Folders (2)", "Files (1)"], node.Children.Select(g => g.Header));
    }

    [Fact]
    public void Writer_SummaryLeavesOutZeroGrowthAndCounts()
    {
        ProcessWriteReport quiet = Writer(1, 1L << 30);
        ProcessWriteReport busy = quiet with { ExtendBytes = 512L << 20, FilesCreated = 2, FilesDeleted = 1 };

        Assert.Equal("1.0 GB written", ProcessNode.From(quiet, quiet.BytesWritten, true).WriteSummary);
        Assert.Equal("1.0 GB written · 512.0 MB growth · 2 created · 1 deleted", ProcessNode.From(busy, busy.BytesWritten, true).WriteSummary);
    }

    [Fact]
    public void Writer_PidAndExePath()
    {
        ProcessWriteReport withExe = Writer(41372, 1) with { ExePath = @"C:\Program Files\PowerShell\7\pwsh.exe" };
        ProcessWriteReport withoutExe = Writer(41372, 1);

        var node = ProcessNode.From(withExe, 1, true);

        Assert.Equal("pid 41372", node.PidText);
        Assert.Equal(@"C:\Program Files\PowerShell\7\pwsh.exe", node.ShortExePath);
        Assert.Equal("", ProcessNode.From(withoutExe, 1, true).ShortExePath);
    }

    [Fact]
    public void FileRow_SizeAndBytesText()
    {
        FileNode measured = new(File(@"C:\a\x.bin", 3L << 30));
        FileNode unmeasured = new(File(@"C:\a\*", null));

        Assert.Equal("now 3.0 GB", measured.SizeText);
        Assert.Equal("1.0 GB written", measured.BytesText);
        Assert.Equal("", unmeasured.SizeText);
        Assert.Equal(@"C:\a", unmeasured.FolderPath);
    }

    [Fact]
    public void Rows_LongPaths_AreShortenedInTheMiddle()
    {
        string longPath = @"C:\Users\leftos\AppData\Local\Temp\" + new string('x', 40) + @"\fsw-fill-test\fill.bin";

        FolderNode folder = new(new FolderWrite(longPath, 1));
        FileNode file = new(File(longPath, null));

        Assert.Equal(AlertFormat.MiddleTrim(longPath, AlertFormat.PathLength), folder.ShortPath);
        Assert.Equal(folder.ShortPath, file.ShortPath);
        Assert.True(folder.ShortPath.Length <= AlertFormat.PathLength);
        Assert.EndsWith(@"\fsw-fill-test\fill.bin", folder.ShortPath, StringComparison.Ordinal);
    }

    private static FileWrite File(string path, long? size) =>
        new()
        {
            Path = path,
            BytesWritten = 1L << 30,
            ExtendBytes = 0,
            Created = true,
            Deleted = false,
            CurrentSize = size,
        };

    private static ProcessWriteReport Writer(int pid, long bytes) =>
        new()
        {
            ProcessId = pid,
            Name = "writer" + pid,
            ExePath = null,
            StartTime = null,
            BytesWritten = bytes,
            ExtendBytes = 0,
            FilesCreated = 0,
            FilesDeleted = 0,
            Folders = [],
            Files = [],
        };

    private static Alert Alert(IReadOnlyList<ProcessWriteReport> writers) =>
        new()
        {
            Id = "a",
            Time = T0,
            Drive = "C",
            Trigger = TriggerKind.DropRate,
            IsEscalation = false,
            Reason = "C: losing 2.1 GB/min",
            FreeBytes = 1L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 0,
            TimeToFull = null,
            UnattributedBytes = 0,
            Processes = writers,
            Acknowledged = false,
        };
}
