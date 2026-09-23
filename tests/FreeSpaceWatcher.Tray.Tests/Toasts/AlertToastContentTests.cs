using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Toasts;

namespace FreeSpaceWatcher.Tray.Tests.Toasts;

public sealed class AlertToastContentTests
{
    private const long GiB = 1L << 30;
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);

    // 11.2 GiB/min, the drive at 61.5 GiB free of 100 GiB.
    private const double LossPerSecond = 11.2 * GiB / 60;
    private const long Free = (long)(61.5 * GiB);
    private const long Total = 100 * GiB;

    [Theory]
    [InlineData(TriggerKind.DropRate)]
    [InlineData(TriggerKind.TimeToFull)]
    public void From_RateAlertWithAnEta_LeadsWithTheEta(TriggerKind trigger)
    {
        var content = AlertToastContent.From(Alert(trigger, TimeSpan.FromMinutes(6), withWriter: true), floorBytes: 5 * GiB);

        Assert.Equal("C: full in ~6 min", content.Title);
        Assert.Equal("Losing 11.2 GB/min · 61.5 GB free", content.Body);
        Assert.Equal(AlertToastContent.CriticalStatus, content.ProgressStatus);
    }

    [Theory]
    [InlineData(TriggerKind.DropRate)]
    [InlineData(TriggerKind.TimeToFull)]
    public void From_RateAlertWithoutAnEta_LeadsWithTheLossRate(TriggerKind trigger)
    {
        var content = AlertToastContent.From(Alert(trigger, eta: null, withWriter: true), floorBytes: 5 * GiB);

        Assert.Equal("C: losing 11.2 GB/min", content.Title);
        Assert.Equal("Losing 11.2 GB/min · 61.5 GB free", content.Body);
        Assert.Equal(AlertToastContent.LowStatus, content.ProgressStatus);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void From_FloorAlert_NamesTheFloor_WithOrWithoutAnEta(bool withEta)
    {
        TimeSpan? eta = withEta ? TimeSpan.FromMinutes(6) : null;

        var content = AlertToastContent.From(Alert(TriggerKind.Floor, eta, withWriter: true), floorBytes: 5 * GiB);

        Assert.Equal("C: below 5.0 GB floor", content.Title);
        Assert.Equal(AlertToastContent.CriticalStatus, content.ProgressStatus);
    }

    [Fact]
    public void From_FloorAlert_WithoutAConfiguration_SaysItsFloor() =>
        Assert.Equal("C: below its floor", AlertToastContent.From(Alert(TriggerKind.Floor, null, withWriter: false), floorBytes: null).Title);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void From_ProcessWriteAlert_NamesTheWriterAndItsBytes_WithOrWithoutAnEta(bool withEta)
    {
        TimeSpan? eta = withEta ? TimeSpan.FromMinutes(6) : null;

        var content = AlertToastContent.From(Alert(TriggerKind.ProcessWriteVolume, eta, withWriter: true), floorBytes: 5 * GiB);

        Assert.Equal("C: pwsh wrote 10.2 GB", content.Title);
        Assert.Equal(withEta ? AlertToastContent.CriticalStatus : AlertToastContent.LowStatus, content.ProgressStatus);
    }

    [Fact]
    public void From_ProcessWriteAlert_WithoutAWriter_FallsBackToTheReason()
    {
        Alert alert = Alert(TriggerKind.ProcessWriteVolume, null, withWriter: false);

        Assert.Equal(alert.Reason, AlertToastContent.From(alert, floorBytes: 5 * GiB).Title);
    }

    [Theory]
    [InlineData(TriggerKind.DropRate)]
    [InlineData(TriggerKind.TimeToFull)]
    [InlineData(TriggerKind.Floor)]
    [InlineData(TriggerKind.ProcessWriteVolume)]
    public void From_WithAWriter_AttributesToItAndItsTopFolder(TriggerKind trigger)
    {
        var content = AlertToastContent.From(Alert(trigger, null, withWriter: true), floorBytes: 5 * GiB);

        Assert.Equal(@"pwsh → C:\Users\…\Temp\fsw-fill-test", content.Attribution);
    }

    [Theory]
    [InlineData(TriggerKind.DropRate)]
    [InlineData(TriggerKind.TimeToFull)]
    [InlineData(TriggerKind.Floor)]
    [InlineData(TriggerKind.ProcessWriteVolume)]
    public void From_WithoutAWriter_SaysNoTracedWriter(TriggerKind trigger) =>
        Assert.Equal(
            AlertToastContent.NoTracedWriter,
            AlertToastContent.From(Alert(trigger, null, withWriter: false), floorBytes: 5 * GiB).Attribution
        );

    [Fact]
    public void From_WriterWithoutFolders_AttributesToTheWriterAlone()
    {
        Alert alert = Alert(TriggerKind.DropRate, null, withWriter: true) with { Processes = [Writer(folders: [])] };

        Assert.Equal("pwsh", AlertToastContent.From(alert, floorBytes: null).Attribution);
    }

    [Fact]
    public void From_ProgressBar_ShowsTheDriveItsUsedFractionAndFreeSpace()
    {
        var content = AlertToastContent.From(Alert(TriggerKind.DropRate, null, withWriter: false), floorBytes: null);

        Assert.Equal("C:", content.ProgressTitle);
        Assert.Equal(0.385, content.UsedFraction, 3);
        Assert.Equal("61.5 GB free", content.ProgressValue);
    }

    [Fact]
    public void From_DriveNotLosingSpace_ShowsOnlyTheFreeSpace()
    {
        Alert alert = Alert(TriggerKind.Floor, null, withWriter: false) with { DropRateBytesPerSecond = 0 };

        Assert.Equal("61.5 GB free", AlertToastContent.From(alert, floorBytes: 5 * GiB).Body);
    }

    [Fact]
    public void From_RateAlertBelowTheFloor_IsCritical()
    {
        Alert alert = Alert(TriggerKind.DropRate, null, withWriter: false);

        Assert.Equal(AlertToastContent.CriticalStatus, AlertToastContent.From(alert, floorBytes: 70 * GiB).ProgressStatus);
    }

    [Fact]
    public void From_ZeroSizedDrive_HasAnEmptyBar() =>
        Assert.Equal(0, AlertToastContent.From(Alert(TriggerKind.Floor, null, false) with { TotalBytes = 0, FreeBytes = 0 }, null).UsedFraction);

    private static Alert Alert(TriggerKind trigger, TimeSpan? eta, bool withWriter) =>
        new()
        {
            Id = "20260923-141500-C-" + trigger,
            Time = T0,
            Drive = "C",
            Trigger = trigger,
            IsEscalation = false,
            Reason = "C: pwsh (pid 41372) wrote 10.2 GB within the write window",
            FreeBytes = Free,
            TotalBytes = Total,
            DropRateBytesPerSecond = LossPerSecond,
            TimeToFull = eta,
            UnattributedBytes = 0,
            Processes = withWriter ? [Writer([new FolderWrite(@"C:\Users\leftos\AppData\Local\Temp\fsw-fill-test", 10 * GiB)])] : [],
            Acknowledged = false,
        };

    private static ProcessWriteReport Writer(IReadOnlyList<FolderWrite> folders) =>
        new()
        {
            ProcessId = 41372,
            Name = "pwsh",
            ExePath = @"C:\Program Files\PowerShell\7\pwsh.exe",
            StartTime = null,
            BytesWritten = (long)(10.2 * GiB),
            ExtendBytes = 0,
            FilesCreated = 0,
            FilesDeleted = 0,
            Folders = folders,
            Files = [],
        };
}
