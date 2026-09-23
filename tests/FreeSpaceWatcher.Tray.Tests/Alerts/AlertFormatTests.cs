using System.Globalization;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Alerts;

namespace FreeSpaceWatcher.Tray.Tests.Alerts;

public sealed class AlertFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 14, 15, 0, TimeSpan.Zero);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData((59 * 60) + 59, "59 min ago")]
    [InlineData(60 * 60, "1 h ago")]
    [InlineData((23 * 3600) + 3599, "23 h ago")]
    [InlineData(24 * 3600, "Sep 22, 14:15")]
    [InlineData(-30, "just now")]
    public void RelativeTime_Boundaries(int secondsAgo, string expected)
    {
        string text = AlertFormat.RelativeTime(Now.AddSeconds(-secondsAgo), Now, TimeZoneInfo.Utc, Invariant);

        Assert.Equal(expected, text);
    }

    [Fact]
    public void RelativeTime_OlderThanADay_IsShownInTheGivenZone()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus-two", TimeSpan.FromHours(2), "plus-two", "plus-two");

        string text = AlertFormat.RelativeTime(Now.AddDays(-3), Now, plusTwo, Invariant);

        Assert.Equal("Sep 20, 16:15", text);
    }

    [Fact]
    public void AbsoluteTime_UsesTheCultureShortDateAndTime() =>
        Assert.Equal("09/23/2026 14:15", AlertFormat.AbsoluteTime(Now, TimeZoneInfo.Utc, Invariant));

    [Theory]
    [InlineData(TriggerKind.DropRate, "Drop rate")]
    [InlineData(TriggerKind.TimeToFull, "Time to full")]
    [InlineData(TriggerKind.Floor, "Floor")]
    [InlineData(TriggerKind.ProcessWriteVolume, "Write volume")]
    public void TriggerName_EveryKind(TriggerKind kind, string expected) => Assert.Equal(expected, AlertFormat.TriggerName(kind));

    [Fact]
    public void MiddleTrim_Fits_IsUnchanged()
    {
        const string path = @"C:\Users\leftos\fill.bin";

        Assert.Equal(path, AlertFormat.MiddleTrim(path, path.Length));
    }

    [Fact]
    public void MiddleTrim_Long_KeepsTheDriveAndTheLastSegments()
    {
        const string path = @"C:\Users\leftos\AppData\Local\Temp\fsw-fill-test\fill.bin";

        Assert.Equal(@"C:\Users\…\fsw-fill-test\fill.bin", AlertFormat.MiddleTrim(path, 36));
    }

    [Fact]
    public void MiddleTrim_PrefersSegmentsFromTheEnd_ThenTheStart()
    {
        const string path = @"C:\a\b\c\d\e\f\g.txt";

        Assert.Equal(@"C:\…\f\g.txt", AlertFormat.MiddleTrim(path, 12));
        Assert.Equal(@"C:\a\…\f\g.txt", AlertFormat.MiddleTrim(path, 14));
        Assert.Equal(@"C:\a\…\e\f\g.txt", AlertFormat.MiddleTrim(path, 16));
    }

    [Fact]
    public void MiddleTrim_TakesTheOtherSide_WhenTheNextOneDoesNotFit()
    {
        const string path = @"C:\a\b\c\a-very-long-folder-name\g.txt";

        Assert.Equal(@"C:\a\b\c\…\g.txt", AlertFormat.MiddleTrim(path, 16));
    }

    [Fact]
    public void MiddleTrim_LastSegmentAloneTooLong_KeepsRootAndLastSegment()
    {
        const string path = @"C:\folder\another\a-file-name-longer-than-the-limit.bin";

        Assert.Equal(@"C:\…\a-file-name-longer-than-the-limit.bin", AlertFormat.MiddleTrim(path, 20));
    }

    [Fact]
    public void MiddleTrim_UncPath_KeepsTheShare()
    {
        const string path = @"\\server\share\projects\builds\output\big.log";

        Assert.Equal(@"\\server\share\…\output\big.log", AlertFormat.MiddleTrim(path, 32));
    }

    [Fact]
    public void MiddleTrim_OneSegment_IsUnchanged()
    {
        const string path = @"C:\a-single-very-long-file-name.bin";

        Assert.Equal(path, AlertFormat.MiddleTrim(path, 10));
    }

    [Fact]
    public void MiddleTrim_FoldedFiles_KeepsTheStar()
    {
        const string path = @"C:\Users\leftos\AppData\Local\Temp\fsw-fill-test\*";

        Assert.Equal(@"C:\Users\…\Temp\fsw-fill-test\*", AlertFormat.MiddleTrim(path, 32));
    }

    [Fact]
    public void MiddleTrim_ZeroLength_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => AlertFormat.MiddleTrim(@"C:\a\b", 0));

    [Theory]
    [InlineData(0, 0, 0.0)]
    [InlineData(5, 0, 0.0)]
    [InlineData(5, 10, 0.5)]
    [InlineData(10, 10, 1.0)]
    [InlineData(20, 10, 1.0)]
    [InlineData(-5, 10, 0.0)]
    public void WriterFraction_IsRelativeToTheTopWriter_AndClamped(long bytes, long topBytes, double expected) =>
        Assert.Equal(expected, AlertFormat.WriterFraction(bytes, topBytes), 6);

    [Theory]
    [InlineData(10, 10, "top writer")]
    [InlineData(20, 10, "top writer")]
    [InlineData(0, 0, "top writer")]
    [InlineData(1, 1000, "< 1 % of top writer")]
    [InlineData(5, 10, "50 % of top writer")]
    [InlineData(62, 100, "62 % of top writer")]
    [InlineData(125, 200, "62 % of top writer")]
    [InlineData(199, 200, "99 % of top writer")]
    public void WriterShareText_IsWholePercentOfTheTopWriter_OrTopWriter(long bytes, long topBytes, string expected) =>
        Assert.Equal(expected, AlertFormat.WriterShareText(bytes, topBytes));
}
