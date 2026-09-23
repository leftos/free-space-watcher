using System.Globalization;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;
using FreeSpaceWatcher.Tray.Settings;

namespace FreeSpaceWatcher.Tray.Tests.Settings;

public sealed class SettingsConversionTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData("1", 1L << 30)]
    [InlineData("1.5", 1610612736L)]
    [InlineData(" 20 ", 20L << 30)]
    public void Parse_Gigabytes_ConvertsToBytes(string text, long bytes)
    {
        ParsedInput parsed = UnitInput.Parse(text, InputUnit.Gigabytes, false, Invariant);

        Assert.Null(parsed.Error);
        Assert.Equal(bytes, parsed.Value);
    }

    [Fact]
    public void Format_Bytes_ShowsGigabytes()
    {
        Assert.Equal("5", UnitInput.Format(5L << 30, InputUnit.Gigabytes, Invariant));
        Assert.Equal("1.5", UnitInput.Format(1610612736L, InputUnit.Gigabytes, Invariant));
        Assert.Equal("50", UnitInput.Format(50L << 20, InputUnit.MegabytesPerMinute, Invariant));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData(null, true)]
    public void Parse_BlankOverride_IsNullWithoutError(string? text, bool allowBlank)
    {
        ParsedInput parsed = UnitInput.Parse(text, InputUnit.Gigabytes, allowBlank, Invariant);

        Assert.Null(parsed.Value);
        Assert.Null(parsed.Error);
    }

    [Fact]
    public void Parse_BlankRequiredField_IsAnError() => Assert.NotNull(UnitInput.Parse("", InputUnit.Gigabytes, false, Invariant).Error);

    [Theory]
    [InlineData("abc")]
    [InlineData("1,2,3")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("1e400")]
    [InlineData("NaN")]
    public void Parse_InvalidText_ReturnsErrorWithoutThrowing(string text)
    {
        ParsedInput parsed = UnitInput.Parse(text, InputUnit.Gigabytes, true, Invariant);

        Assert.Null(parsed.Value);
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void Parse_PercentOverHundred_IsAnError() => Assert.NotNull(UnitInput.Parse("150", InputUnit.Percent, true, Invariant).Error);

    [Fact]
    public void Parse_FractionForWholeUnit_IsAnError() => Assert.NotNull(UnitInput.Parse("1.5", InputUnit.Seconds, false, Invariant).Error);

    [Fact]
    public void Load_ShowsValuesInHumanUnitsAndListsReadyDrivesNotInConfig()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), ["C", "E"]);

        viewModel.Load(new ConfigResponse(SampleConfig(), "bad json"));

        Assert.Equal(["C", "D", "E"], viewModel.Drives.Select(d => d.Letter));
        DriveRow c = viewModel.Drives[0];
        Assert.True(c.Watched);
        Assert.Equal("20", c.FloorBytes.Text);
        Assert.True(c.DropRate.IsBlank);
        Assert.Equal("1", c.DropRate.Placeholder);
        Assert.False(viewModel.Drives[2].Watched);
        Assert.Equal("1", viewModel.DefaultDropRate.Text);
        Assert.Equal("50", viewModel.DefaultNoiseFloor.Text);
        Assert.Equal("bad json", viewModel.LoadError);
    }

    [Fact]
    public void BuildConfig_TypedGigabytesBecomeBytesAndBlankOverridesBecomeNull()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));
        DriveRow c = viewModel.Drives[0];
        c.DropRate.Text = "2.5";
        c.FloorBytes.Text = "";
        viewModel.DefaultFloorBytes.Text = "8";

        WatcherConfig? config = viewModel.BuildConfig();

        Assert.NotNull(config);
        Thresholds overrides = config.Drives[0].Overrides;
        Assert.Equal((long)(2.5 * (1L << 30)), overrides.DropRateBytesPerMinute);
        Assert.Null(overrides.FloorBytes);
        Assert.Null(overrides.NoiseFloorBytesPerMinute);
        Assert.Equal(8L << 30, config.Defaults.FloorBytes);
        Assert.Equal(SampleConfig().PerProcessFileCap, config.PerProcessFileCap);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("45", 45)]
    [InlineData("600", 600)]
    public void Parse_GraceSeconds_AcceptsZeroAsOff_UpTo600(string text, double seconds)
    {
        ParsedInput parsed = UnitInput.Parse(text, InputUnit.GraceSeconds, true, Invariant);

        Assert.Null(parsed.Error);
        Assert.Equal(seconds, parsed.Value);
    }

    [Theory]
    [InlineData("-1", "Must be 0 or more")]
    [InlineData("1.5", "Must be a whole number")]
    [InlineData("1441", "Must be at most 1440")]
    public void Parse_ResolveMinutes_RejectsNegativeFractionAndOverADay(string text, string error)
    {
        ParsedInput parsed = UnitInput.Parse(text, InputUnit.ResolveMinutes, true, Invariant);

        Assert.Null(parsed.Value);
        Assert.Equal(error, parsed.Error);
    }

    [Fact]
    public void Parse_ZeroInAUnitWithoutOff_IsStillAnError() =>
        Assert.Equal("Must be greater than 0", UnitInput.Parse("0", InputUnit.Seconds, false, Invariant).Error);

    [Fact]
    public void Load_ShowsCleanupDefaults_AndDriveOverridesWithTheDefaultsAsPlaceholders()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), []);
        WatcherConfig config = SampleConfig() with
        {
            Drives = [new DriveConfig("C", true, new Thresholds { GraceSeconds = 0 }), new DriveConfig("D", false, new Thresholds())],
        };

        viewModel.Load(new ConfigResponse(config, null));

        Assert.Equal("20", viewModel.DefaultGraceDelay.Text);
        Assert.Equal("5", viewModel.DefaultResolveWindow.Text);
        DriveRow c = viewModel.Drives[0];
        Assert.Equal("0", c.GraceDelay.Text);
        Assert.True(c.ThresholdsExpanded);
        Assert.True(c.ResolveWindow.IsBlank);
        Assert.Equal("5", c.ResolveWindow.Placeholder);
        Assert.Equal("20", viewModel.Drives[1].GraceDelay.Placeholder);
    }

    [Fact]
    public void BuildConfig_CleanupFields_ZeroTurnsOff_BlankOverrideUsesTheDefault()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));
        viewModel.DefaultGraceDelay.Text = "0";
        viewModel.DefaultResolveWindow.Text = "15";
        DriveRow c = viewModel.Drives[0];
        c.GraceDelay.Text = "45";
        c.ResolveWindow.Text = "0";

        WatcherConfig? config = viewModel.BuildConfig();

        Assert.NotNull(config);
        Assert.Equal((0, 15), (config.Defaults.GraceSeconds, config.Defaults.ResolveMinutes));
        Assert.Equal((45, 0), (config.Drives[0].Overrides.GraceSeconds, config.Drives[0].Overrides.ResolveMinutes));
        Assert.Equal((null, null), (config.Drives[1].Overrides.GraceSeconds, config.Drives[1].Overrides.ResolveMinutes));
        Assert.Equal((0, 15), (config.For("D").GraceSeconds, config.For("D").ResolveMinutes));
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void BuildConfig_CleanupFieldOutOfRange_IsAFieldError_AndBuildsNothing()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));

        viewModel.Drives[0].GraceDelay.Text = "601";
        viewModel.DefaultResolveWindow.Text = "";

        Assert.Null(viewModel.BuildConfig());
        Assert.Equal("Must be at most 600", viewModel.Drives[0].GraceDelay.Error);
        Assert.Equal("Required", viewModel.DefaultResolveWindow.Error);
        Assert.Equal(["Fix the highlighted fields before saving."], viewModel.Errors);
    }

    [Fact]
    public async Task Save_InvalidText_ShowsFieldErrorAndSendsNothing()
    {
        FakeChannel channel = new();
        SettingsViewModel viewModel = NewViewModel(channel, []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));
        viewModel.IsConnected = true;

        viewModel.Drives[0].DropRate.Text = "lots";
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.Drives[0].DropRate.Error);
        Assert.NotEmpty(viewModel.Errors);
        Assert.Empty(channel.Sent);
    }

    [Fact]
    public async Task Save_ServiceRejects_ShowsItsErrorsAndDoesNotReportSuccess()
    {
        FakeChannel channel = new() { Respond = _ => new SetConfigResponse(false, ["Rate window must be at least 5 samples."]) };
        SettingsViewModel viewModel = NewViewModel(channel, []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));
        viewModel.IsConnected = true;
        bool saved = false;
        viewModel.SaveSucceeded += (_, _) => saved = true;

        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.IsType<SetConfigRequest>(Assert.Single(channel.Sent));
        Assert.Equal(["Rate window must be at least 5 samples."], viewModel.Errors);
        Assert.False(saved);
    }

    [Fact]
    public void SaveCommand_Disconnected_CannotExecute()
    {
        SettingsViewModel viewModel = NewViewModel(new FakeChannel(), []);
        viewModel.Load(new ConfigResponse(SampleConfig(), null));

        viewModel.IsConnected = false;

        Assert.False(viewModel.SaveCommand.CanExecute(null));
    }

    private static SettingsViewModel NewViewModel(FakeChannel channel, string[] readyDrives) => new(channel, () => readyDrives, Invariant);

    private static WatcherConfig SampleConfig() =>
        WatcherConfig.CreateDefault(["D"]) with
        {
            Drives = [new DriveConfig("C", true, new Thresholds { FloorBytes = 20L << 30 }), new DriveConfig("D", false, new Thresholds())],
            PerProcessFileCap = 1234,
        };

    private sealed class FakeChannel : IServiceChannel
    {
        public Func<PipeMessage, PipeMessage> Respond { get; init; } = request => throw new InvalidOperationException($"Unexpected {request}");

        public List<PipeMessage> Sent { get; } = [];

        public bool IsConnected => true;

        public Task<TResponse> SendAsync<TResponse>(PipeMessage request, CancellationToken cancellationToken)
            where TResponse : PipeMessage
        {
            Sent.Add(request);
            return Task.FromResult((TResponse)Respond(request));
        }
    }
}
