namespace FreeSpaceWatcher.Tray.Tests;

public sealed class TrayArgumentsTests
{
    [Fact]
    public void NoArguments_SystemThemeAndNothingToOpen()
    {
        var parsed = TrayArguments.Parse([]);

        Assert.Equal(TrayTheme.System, parsed.Theme);
        Assert.Null(parsed.Open);
        Assert.Empty(parsed.Problems);
    }

    [Theory]
    [InlineData("light", TrayTheme.Light)]
    [InlineData("DARK", TrayTheme.Dark)]
    [InlineData("system", TrayTheme.System)]
    public void Theme_Valid_IsApplied(string value, TrayTheme expected)
    {
        var parsed = TrayArguments.Parse(["--theme", value]);

        Assert.Equal(expected, parsed.Theme);
        Assert.Empty(parsed.Problems);
    }

    [Theory]
    [InlineData("alerts", TrayWindow.Alerts)]
    [InlineData("Settings", TrayWindow.Settings)]
    [InlineData("status", TrayWindow.Status)]
    public void Open_Valid_IsApplied(string value, TrayWindow expected)
    {
        var parsed = TrayArguments.Parse(["--open", value]);

        Assert.Equal(expected, parsed.Open);
        Assert.Empty(parsed.Problems);
    }

    [Fact]
    public void ThemeAndOpen_Combine_InEitherOrder()
    {
        var parsed = TrayArguments.Parse(["--open", "status", "--theme", "dark"]);

        Assert.Equal(TrayTheme.Dark, parsed.Theme);
        Assert.Equal(TrayWindow.Status, parsed.Open);
        Assert.Empty(parsed.Problems);
    }

    [Theory]
    [InlineData("--theme", "blue")]
    [InlineData("--theme", "1")]
    [InlineData("--open", "history")]
    [InlineData("--open", "2")]
    [InlineData("--open", "alerts,settings")]
    public void UnknownValue_IsIgnoredAndReported(string option, string value)
    {
        var parsed = TrayArguments.Parse([option, value]);

        Assert.Equal(TrayTheme.System, parsed.Theme);
        Assert.Null(parsed.Open);
        Assert.Contains(value, Assert.Single(parsed.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--theme")]
    [InlineData("--open")]
    public void MissingValue_IsIgnoredAndReported(string option)
    {
        var parsed = TrayArguments.Parse(["--theme", "light", option]);

        Assert.Equal(TrayTheme.Light, parsed.Theme);
        Assert.Null(parsed.Open);
        Assert.Equal($"Ignored {option}: it needs a value.", Assert.Single(parsed.Problems));
    }

    [Fact]
    public void SelectLatest_WithOpenAlerts_IsApplied_InEitherOrder()
    {
        var before = TrayArguments.Parse(["--select-latest", "--open", "alerts"]);
        var after = TrayArguments.Parse(["--open", "alerts", "--SELECT-LATEST"]);

        Assert.True(before.SelectLatest);
        Assert.True(after.SelectLatest);
        Assert.Empty(before.Problems);
        Assert.Empty(after.Problems);
    }

    [Fact]
    public void SelectLatest_Absent_IsOff() => Assert.False(TrayArguments.Parse(["--open", "alerts"]).SelectLatest);

    [Theory]
    [InlineData("--open", "settings")]
    [InlineData("--theme", "dark")]
    public void SelectLatest_WithoutOpenAlerts_IsIgnoredAndReported(string option, string value)
    {
        var parsed = TrayArguments.Parse(["--select-latest", option, value]);

        Assert.False(parsed.SelectLatest);
        Assert.Equal("Ignored --select-latest: it needs --open alerts.", Assert.Single(parsed.Problems));
    }

    [Fact]
    public void UnknownArgument_IsIgnoredAndReported_AndTheRestStillParse()
    {
        var parsed = TrayArguments.Parse(["--verbose", "--open", "settings"]);

        Assert.Equal(TrayWindow.Settings, parsed.Open);
        Assert.Equal("Ignored unknown argument '--verbose'.", Assert.Single(parsed.Problems));
    }
}
