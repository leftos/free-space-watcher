using FreeSpaceWatcher.Service.Etw;

namespace FreeSpaceWatcher.Service.Tests.Etw;

public sealed class DevicePathMapperTests
{
    [Fact]
    public void ToDosPath_PicksTheLongestMatchingDevice()
    {
        DevicePathMapper mapper = MapperFor(
            new Dictionary<char, string>
            {
                ['C'] = @"\Device\HarddiskVolume3",
                ['M'] = @"\Device\HarddiskVolume3\Data",
                ['D'] = @"\Device\HarddiskVolume1",
                ['E'] = @"\Device\HarddiskVolume10",
            }
        );

        Assert.Equal(@"M:\logs\app.log", mapper.ToDosPath(@"\Device\HarddiskVolume3\Data\logs\app.log"));
        Assert.Equal(@"C:\Data2\file.bin", mapper.ToDosPath(@"\Device\HarddiskVolume3\Data2\file.bin"));
        Assert.Equal(@"C:\Windows\temp.tmp", mapper.ToDosPath(@"\Device\HarddiskVolume3\Windows\temp.tmp"));
        Assert.Equal(@"E:\big.bin", mapper.ToDosPath(@"\Device\HarddiskVolume10\big.bin"));
        Assert.Equal(@"D:\small.bin", mapper.ToDosPath(@"\device\harddiskvolume1\small.bin"));
    }

    [Theory]
    [InlineData(@"\Device\Mup\server\share\file.txt")]
    [InlineData(@"\Device\HarddiskVolume7\file.txt")]
    [InlineData(@"\??\Volume{3f2504e0-4f89-11d3-9a0c-0305e82c3301}\file.txt")]
    [InlineData(@"\Device\HarddiskVolume30\file.txt")]
    [InlineData("")]
    public void ToDosPath_UnmappedPath_ReturnsNull(string ntPath)
    {
        DevicePathMapper mapper = MapperFor(new Dictionary<char, string> { ['C'] = @"\Device\HarddiskVolume3" });

        Assert.Null(mapper.ToDosPath(ntPath));
    }

    private static DevicePathMapper MapperFor(Dictionary<char, string> devices) =>
        new(letter => devices.GetValueOrDefault(letter), TimeProvider.System);
}
