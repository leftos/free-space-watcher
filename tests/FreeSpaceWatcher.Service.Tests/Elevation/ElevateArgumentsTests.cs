using FreeSpaceWatcher.Elevate;
using FreeSpaceWatcher.Native;

namespace FreeSpaceWatcher.Service.Tests.Elevation;

public sealed class ElevateArgumentsTests
{
    public static TheoryData<string[]> Malformed =>
        new(
            [],
            ["kill"],
            ["stop", "1234"],
            ["Kill", "1234"],
            ["kill", "abc"],
            ["kill", "-5"],
            ["kill", "0"],
            ["kill", "1234", "yesterday"],
            ["kill", "1234", "-1"],
            ["kill", "1234", "638940000000000000", "extra"]
        );

    [Theory]
    [InlineData("suspend", ProcessControlAction.Suspend)]
    [InlineData("resume", ProcessControlAction.Resume)]
    [InlineData("kill", ProcessControlAction.Terminate)]
    public void Parse_EachVerbWithPid_MapsToItsAction(string verb, ProcessControlAction expected)
    {
        ElevateRequest? request = ElevateArguments.Parse([verb, "1234"]);

        Assert.Equal(new ElevateRequest(expected, 1234, null), request);
    }

    [Fact]
    public void Parse_StartTimeTicks_AreReadAsUtc()
    {
        DateTimeOffset start = new(2026, 9, 23, 10, 15, 30, TimeSpan.Zero);

        ElevateRequest? request = ElevateArguments.Parse([
            "suspend",
            "1234",
            start.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ]);

        Assert.NotNull(request);
        Assert.Equal(start, request.StartTime);
        Assert.Equal(TimeSpan.Zero, request.StartTime?.Offset);
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Parse_MalformedArguments_ReturnsNull(string[] args) => Assert.Null(ElevateArguments.Parse(args));
}
