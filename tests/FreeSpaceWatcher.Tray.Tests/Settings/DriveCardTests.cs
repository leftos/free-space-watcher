using System.Globalization;
using System.Windows.Data;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Settings;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Tests.Settings;

public sealed class DriveCardTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(null, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public void OverrideChoice_RoundTrips(bool? enabled, int index)
    {
        Assert.Equal(index, TriggerChoice.OverrideIndex(enabled));
        Assert.Equal(enabled, TriggerChoice.OverrideValue(index));
        Assert.Equal(["Use default", "On", "Off"], TriggerChoice.OverrideLabels);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public void DefaultChoice_RoundTrips(bool enabled, int index)
    {
        Assert.Equal(index, TriggerChoice.DefaultIndex(enabled));
        Assert.Equal(enabled, TriggerChoice.DefaultValue(index));
        Assert.Equal(["On", "Off"], TriggerChoice.DefaultLabels);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Choices_OutOfRange_Throw(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerChoice.OverrideValue(index));
        Assert.Throws<ArgumentOutOfRangeException>(() => TriggerChoice.DefaultValue(index));
    }

    [Fact]
    public void Converters_MapBothWays_AndIgnoreAClearedSelection()
    {
        OverrideChoiceConverter overrides = new();
        DefaultChoiceConverter defaults = new();

        Assert.Equal(0, overrides.Convert(null, typeof(int), null, Invariant));
        Assert.Equal(2, overrides.Convert(false, typeof(int), null, Invariant));
        Assert.Null(overrides.ConvertBack(0, typeof(bool?), null, Invariant));
        Assert.Equal(true, overrides.ConvertBack(1, typeof(bool?), null, Invariant));
        Assert.Same(Binding.DoNothing, overrides.ConvertBack(-1, typeof(bool?), null, Invariant));
        Assert.Equal(1, defaults.Convert(false, typeof(int), null, Invariant));
        Assert.Equal(true, defaults.ConvertBack(0, typeof(bool), null, Invariant));
        Assert.Same(Binding.DoNothing, defaults.ConvertBack(-1, typeof(bool), null, Invariant));
    }

    [Fact]
    public void Load_CustomThresholds_OpenOnlyForADriveWithAnOverride()
    {
        DriveRow valueOverride = new("C", Invariant);
        DriveRow toggleOverride = new("D", Invariant);
        DriveRow none = new("E", Invariant);

        valueOverride.Load(new Thresholds { FloorBytes = 20L << 30 });
        toggleOverride.Load(new Thresholds { DropRateEnabled = false });
        none.Load(new Thresholds());

        Assert.True(valueOverride.ThresholdsExpanded);
        Assert.True(toggleOverride.ThresholdsExpanded);
        Assert.False(none.ThresholdsExpanded);
    }

    [Fact]
    public void ApplyStatus_ShowsFreeOfTotal_TheBarAndItsSeverity()
    {
        DriveRow row = new("C", Invariant);
        ResolvedThresholds thresholds = ResolvedThresholds.Default with { FloorBytes = 10L << 30, FloorPercent = 1 };

        row.ApplyStatus(Status(free: 25L << 30, total: 100L << 30), thresholds);

        Assert.Equal("25.0 GB free of 100.0 GB", row.FreeText);
        Assert.Equal(0.75, row.UsedFraction, 6);
        Assert.Equal(DriveSeverity.Healthy, row.Severity);

        row.ApplyStatus(Status(free: 15L << 30, total: 100L << 30), thresholds);
        Assert.Equal(DriveSeverity.Caution, row.Severity);

        row.ApplyStatus(Status(free: 5L << 30, total: 100L << 30), thresholds);
        Assert.Equal(DriveSeverity.Critical, row.Severity);
    }

    [Fact]
    public void ApplyStatus_UnavailableOrMissing_ShowsNoBar()
    {
        DriveRow row = new("C", Invariant);
        row.ApplyStatus(Status(free: 5L << 30, total: 100L << 30), ResolvedThresholds.Default);

        row.ApplyStatus(Status(free: 0, total: 0) with { Available = false }, ResolvedThresholds.Default);
        Assert.Equal("Not available", row.FreeText);
        Assert.Equal(0, row.UsedFraction);

        row.ApplyStatus(null, ResolvedThresholds.Default);
        Assert.Equal(StatusText.Unknown, row.FreeText);
        Assert.Equal(DriveSeverity.Healthy, row.Severity);
    }

    private static DriveStatus Status(long free, long total) =>
        new()
        {
            Letter = "C",
            Watched = true,
            Available = true,
            FreeBytes = free,
            TotalBytes = total,
            DropRateBytesPerSecond = 0,
            TimeToFull = null,
        };
}
