using System.Windows;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Tests.Status;

public sealed class SparklineGeometryTests
{
    private const long Mib = 1L << 20;
    private const long Gib = 1L << 30;

    // A 100 GB drive: 0.5 % of it (512 MB) is more than 256 MB, so its smallest range shown is 512 MB.
    private const long Total = 100 * Gib;
    private static readonly DateTimeOffset End = new(2026, 9, 23, 14, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(600);
    private static readonly Size Area = new(600, 56);

    [Fact]
    public void NoSamples_DrawsNothing()
    {
        var geometry = SparklineGeometry.Map([], End, Span, Area, 100);

        Assert.Empty(geometry.Points);
        Assert.Null(geometry.FloorY);
        Assert.Null(geometry.MinFreeBytes);
        Assert.Null(geometry.MaxFreeBytes);
    }

    [Fact]
    public void NoSamples_EndingAtTheEarliestTime_DrawsNothing()
    {
        // The chart passes DateTimeOffset.MinValue as the end before its first sample arrives.
        var geometry = SparklineGeometry.Map([], DateTimeOffset.MinValue, Span, Area, 100);

        Assert.Empty(geometry.Points);
        Assert.Null(geometry.FloorY);
    }

    [Fact]
    public void SteadySeries_WithKilobytesOfNoise_DrawsFlatAtMidHeight()
    {
        var geometry = SparklineGeometry.Map([At(-600, 50 * Gib), At(-300, (50 * Gib) + 4096), At(0, (50 * Gib) - 4096)], End, Span, Area, null);

        Assert.All(geometry.Points, p => Assert.Equal(28, p.Y, 0.01));
    }

    [Fact]
    public void TenGigabyteRamp_FillsTheHeight_WithFivePercentPadding()
    {
        // 60 GB falling to 50 GB: the padded range is 49.5..60.5 GB, 11 GB tall.
        var geometry = SparklineGeometry.Map([At(-600, 60 * Gib), At(-300, 55 * Gib), At(0, 50 * Gib)], End, Span, Area, null);

        Assert.Equal([0.0, 300.0, 600.0], geometry.Points.Select(p => Math.Round(p.X, 6)));
        Assert.Equal(56 - (10.5 / 11 * 56), geometry.Points[0].Y, 6);
        Assert.Equal(28, geometry.Points[1].Y, 6);
        Assert.Equal(56 - (0.5 / 11 * 56), geometry.Points[2].Y, 6);
        Assert.Equal(50 * Gib, geometry.MinFreeBytes);
        Assert.Equal(60 * Gib, geometry.MaxFreeBytes);
    }

    [Fact]
    public void SamplesBeforeTheSpan_AreLeftOut()
    {
        var geometry = SparklineGeometry.Map([At(-601, 99 * Gib), At(-600, 60 * Gib), At(0, 50 * Gib)], End, Span, Area, null);

        Assert.Equal(2, geometry.Points.Count);
        Assert.Equal(0, geometry.Points[0].X, 6);
        Assert.Equal(60 * Gib, geometry.MaxFreeBytes);
    }

    [Fact]
    public void SinglePoint_IsDrawnFlatToTheRightEdge()
    {
        var geometry = SparklineGeometry.Map([At(-60, 50 * Gib)], End, Span, Area, null);

        Assert.Equal([new Point(540, 28), new Point(600, 28)], geometry.Points);
    }

    [Theory]
    [InlineData(49.6, true)]
    [InlineData(49.4, false)]
    [InlineData(60.6, false)]
    public void Floor_OnARamp_IsDrawnOnlyInsideThePaddedRange(double floorGib, bool drawn)
    {
        var geometry = SparklineGeometry.Map([At(-600, 60 * Gib), At(0, 50 * Gib)], End, Span, Area, (long)(floorGib * Gib));

        Assert.Equal(drawn, geometry.FloorY is not null);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(300, false)]
    public void Floor_OnASteadyBigDrive_IsDrawnWithinAQuarterPercentOfTheValue(long mibBelow, bool drawn)
    {
        var geometry = SparklineGeometry.Map([At(-60, 50 * Gib), At(0, 50 * Gib)], End, Span, Area, (50 * Gib) - (mibBelow * Mib));

        Assert.Equal(drawn, geometry.FloorY is not null);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(200, false)]
    public void Floor_OnASteadySmallDrive_UsesThe256MegabyteMinimum(long mibBelow, bool drawn)
    {
        // A 10 GB drive: 0.5 % is 51 MB, so the range shown is 256 MB, 128 MB either side of the value.
        DriveSample[] samples = [new(End.AddSeconds(-60), 5 * Gib, 10 * Gib), new(End, 5 * Gib, 10 * Gib)];

        var geometry = SparklineGeometry.Map(samples, End, Span, Area, (5 * Gib) - (mibBelow * Mib));

        Assert.Equal(drawn, geometry.FloorY is not null);
    }

    private static DriveSample At(int secondsFromEnd, long freeBytes) => new(End.AddSeconds(secondsFromEnd), freeBytes, Total);
}
