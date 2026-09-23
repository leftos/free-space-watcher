using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Toasts;

namespace FreeSpaceWatcher.Tray.Tests.Toasts;

public sealed class AlertToastsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);

    [Fact]
    public void SummarySlot_IsTaggedSummary_InTheAlertsGroup() => Assert.Equal(new ToastSlot("summary", "alerts"), AlertToasts.SummarySlot);

    [Fact]
    public void SummarySlot_SharesTheGroupOfAlertToasts_ButNotTheirTag()
    {
        ToastSlot alert = AlertToasts.AlertSlot(FullAlert("a", "C"));

        Assert.Equal(new ToastSlot("C", "alerts"), alert);
        Assert.Equal(alert.Group, AlertToasts.SummarySlot.Group);
        Assert.NotEqual(alert.Tag, AlertToasts.SummarySlot.Tag);
    }

    [Fact]
    public void ProcessActionSlot_IsTaggedWithThePid_InTheActionsGroup() =>
        Assert.Equal(new ToastSlot("action-41372", "actions"), AlertToasts.ProcessActionSlot(41372));

    [Fact]
    public void DrivesToClear_DriveLostItsAlert_NoneUnacknowledgedLeft_IsCleared()
    {
        AlertSummary[] before = [Summary("c1", "C", acknowledged: false), Summary("d1", "D", acknowledged: false)];
        AlertSummary[] after = [Summary("d1", "D", acknowledged: false)];

        Assert.Equal(["C"], AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_DriveLostAnAlert_ButStillHasAnUnacknowledgedOne_IsKept()
    {
        AlertSummary[] before = [Summary("c1", "C", acknowledged: false), Summary("c2", "C", acknowledged: false)];
        AlertSummary[] after = [Summary("c2", "C", acknowledged: false)];

        Assert.Empty(AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_DriveLostAnAlert_OnlyAcknowledgedLeft_IsCleared()
    {
        AlertSummary[] before = [Summary("c1", "C", acknowledged: false), Summary("c2", "C", acknowledged: true), Summary("d1", "D", false)];
        AlertSummary[] after = [Summary("c2", "C", acknowledged: true), Summary("d1", "D", acknowledged: false)];

        Assert.Equal(["C"], AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_NothingUnacknowledgedAnywhere_ClearsEveryDrive_Once()
    {
        AlertSummary[] before = [Summary("c1", "C", false), Summary("c2", "c", false), Summary("d1", "D", false), Summary("e1", "E", true)];
        AlertSummary[] after = [Summary("e1", "E", acknowledged: true)];

        Assert.Equal(["C", "D", "E"], AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_DriveAcknowledged_NoAlertLost_IsCleared()
    {
        AlertSummary[] before = [Summary("c1", "C", acknowledged: false)];
        AlertSummary[] after = [Summary("c1", "C", acknowledged: true)];

        Assert.Equal(["C"], AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_CAcknowledged_WhileDStaysUnacknowledged_ClearsOnlyC()
    {
        AlertSummary[] before = [Summary("c1", "C", acknowledged: false), Summary("d1", "D", acknowledged: false)];
        AlertSummary[] after = [Summary("c1", "C", acknowledged: true), Summary("d1", "D", acknowledged: false)];

        Assert.Equal(["C"], AlertToasts.DrivesToClear(before, after));
    }

    [Fact]
    public void DrivesToClear_DriveOnlyInTheNewList_IsNotNamed()
    {
        AlertSummary[] before = [Summary("d1", "D", acknowledged: false)];
        AlertSummary[] after = [Summary("c1", "C", acknowledged: true), Summary("d1", "D", acknowledged: false)];

        Assert.Empty(AlertToasts.DrivesToClear(before, after));
    }

    internal static AlertSummary Summary(string id, string drive, bool acknowledged) =>
        new()
        {
            Id = id,
            Time = T0,
            Drive = drive,
            Trigger = TriggerKind.Floor,
            Reason = $"{drive}: below the floor",
            Acknowledged = acknowledged,
        };

    internal static Alert FullAlert(string id, string drive) =>
        new()
        {
            Id = id,
            Time = T0,
            Drive = drive,
            Trigger = TriggerKind.Floor,
            IsEscalation = false,
            Reason = $"{drive}: below the floor",
            FreeBytes = 1L << 30,
            TotalBytes = 100L << 30,
            DropRateBytesPerSecond = 0,
            TimeToFull = null,
            UnattributedBytes = 0,
            Processes = [],
            Acknowledged = false,
        };
}
