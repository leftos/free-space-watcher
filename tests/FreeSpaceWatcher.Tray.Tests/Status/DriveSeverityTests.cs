using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Tests.Status;

public sealed class DriveSeverityTests
{
    private const long Gib = 1L << 30;

    // A 100 GB drive with a 10 GB byte floor and a 5 % (5 GB) percentage floor: the floor is 10 GB.
    private static readonly ResolvedThresholds Thresholds = ResolvedThresholds.Default with { FloorBytes = 10 * Gib, FloorPercent = 5 };

    [Theory]
    [InlineData(10 * Gib - 1, DriveSeverity.Critical)]
    [InlineData(10 * Gib, DriveSeverity.Caution)]
    [InlineData(20 * Gib - 1, DriveSeverity.Caution)]
    [InlineData(20 * Gib, DriveSeverity.Healthy)]
    public void Boundaries_AtTheFloorAndTwiceTheFloor(long freeBytes, DriveSeverity expected) =>
        Assert.Equal(expected, DriveSeverityRules.For(Drive(freeBytes), Thresholds, hasUnacknowledgedAlert: false));

    [Fact]
    public void UnacknowledgedAlert_OverridesHealthyFreeSpace() =>
        Assert.Equal(DriveSeverity.Critical, DriveSeverityRules.For(Drive(90 * Gib), Thresholds, hasUnacknowledgedAlert: true));

    [Fact]
    public void PercentFloor_WinsWhenLarger()
    {
        ResolvedThresholds percent = Thresholds with { FloorBytes = 1 * Gib, FloorPercent = 30 };

        Assert.Equal(30 * Gib, DriveSeverityRules.FloorBytes(percent, 100 * Gib));
        Assert.Equal(DriveSeverity.Critical, DriveSeverityRules.For(Drive(29 * Gib), percent, hasUnacknowledgedAlert: false));
    }

    [Fact]
    public void FloorApplies_EvenWhenTheFloorTriggerIsDisabled() =>
        Assert.Equal(
            DriveSeverity.Critical,
            DriveSeverityRules.For(Drive(1 * Gib), Thresholds with { FloorEnabled = false }, hasUnacknowledgedAlert: false)
        );

    [Fact]
    public void UnavailableDrive_IsHealthyUnlessAlerting()
    {
        DriveStatus gone = Drive(0) with { Available = false };

        Assert.Equal(DriveSeverity.Healthy, DriveSeverityRules.For(gone, Thresholds, hasUnacknowledgedAlert: false));
        Assert.Equal(DriveSeverity.Critical, DriveSeverityRules.For(gone, Thresholds, hasUnacknowledgedAlert: true));
    }

    private static DriveStatus Drive(long freeBytes) =>
        new()
        {
            Letter = "C",
            Watched = true,
            Available = true,
            FreeBytes = freeBytes,
            TotalBytes = 100 * Gib,
            DropRateBytesPerSecond = null,
            TimeToFull = null,
        };
}
