using FreeSpaceWatcher.Core.Config;

namespace FreeSpaceWatcher.Core.Tests.Config;

public sealed class ConfigValidatorTests
{
    private static WatcherConfig Valid => WatcherConfig.CreateDefault(["D"]);

    [Fact]
    public void DefaultConfig_IsValid() => Assert.Empty(ConfigValidator.Validate(Valid));

    [Fact]
    public void ZeroThreshold_IsRejected()
    {
        string error = SingleError(Valid with { Defaults = Valid.Defaults with { DropRateBytesPerMinute = 0 } });

        Assert.Equal("Defaults: drop rate must be greater than zero (got 0).", error);
    }

    [Fact]
    public void NegativeThreshold_IsRejected()
    {
        string error = SingleError(Valid with { Defaults = Valid.Defaults with { FloorBytes = -1 } });

        Assert.Equal("Defaults: floor size must be greater than zero (got -1).", error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(100.5)]
    public void FloorPercentOutsideRange_IsRejected(double percent)
    {
        string error = SingleError(Valid with { Defaults = Valid.Defaults with { FloorPercent = percent } });

        Assert.StartsWith("Defaults: floor percent must be greater than 0 and at most 100", error);
    }

    [Fact]
    public void FloorPercentOfHundred_IsAccepted() =>
        Assert.Empty(ConfigValidator.Validate(Valid with { Defaults = Valid.Defaults with { FloorPercent = 100 } }));

    [Fact]
    public void SampleIntervalBelowOne_IsRejected()
    {
        string error = SingleError(Valid with { SampleIntervalSeconds = 0 });

        Assert.Equal("Sample interval must be at least 1 second (got 0).", error);
    }

    [Fact]
    public void RateWindowUnderFiveSamples_IsRejected()
    {
        string error = SingleError(Valid with { SampleIntervalSeconds = 20 });

        Assert.StartsWith("Rate window (60 s) must be at least 5 times the sample interval", error);
    }

    [Fact]
    public void WriteWindowShorterThanRateWindow_IsRejected()
    {
        string error = SingleError(Valid with { WriteWindowSeconds = 59 });

        Assert.Equal("Write window (59 s) must not be shorter than the rate window (60 s).", error);
    }

    [Fact]
    public void NegativeCooldown_IsRejected()
    {
        string error = SingleError(Valid with { CooldownMinutes = -1 });

        Assert.Equal("Cooldown must not be negative (got -1).", error);
    }

    [Fact]
    public void DuplicateDriveLetters_AreRejected()
    {
        string error = SingleError(Valid with { Drives = [new("C", true, new Thresholds()), new("c", false, new Thresholds())] });

        Assert.Equal("Drive C is listed more than once.", error);
    }

    [Theory]
    [InlineData("CD")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("C:")]
    public void MalformedDriveLetter_IsRejected(string letter)
    {
        string error = SingleError(Valid with { Drives = [new(letter, true, new Thresholds())] });

        Assert.Equal($"Drive letter '{letter}' is not a single letter A-Z.", error);
    }

    [Fact]
    public void HistoryDaysBelowOne_IsRejected()
    {
        string error = SingleError(Valid with { HistoryDays = 0 });

        Assert.Equal("History days must be at least 1 (got 0).", error);
    }

    [Theory]
    [InlineData("Per-process file cap")]
    [InlineData("Top processes")]
    [InlineData("Top folders")]
    [InlineData("Top files")]
    public void CapBelowOne_IsRejected(string name)
    {
        WatcherConfig config = name switch
        {
            "Per-process file cap" => Valid with { PerProcessFileCap = 0 },
            "Top processes" => Valid with { TopProcesses = 0 },
            "Top folders" => Valid with { TopFolders = 0 },
            _ => Valid with { TopFiles = 0 },
        };

        string error = SingleError(config);

        Assert.Equal($"{name} must be at least 1 (got 0).", error);
    }

    [Fact]
    public void DriveOverrideInvalid_IsRejected()
    {
        string error = SingleError(Valid with { Drives = [new("C", true, new Thresholds { TimeToFullMinutes = -1 })] });

        Assert.Equal("Drive C: time to full must be greater than zero (got -1).", error);
    }

    private static string SingleError(WatcherConfig config) => Assert.Single(ConfigValidator.Validate(config));
}
