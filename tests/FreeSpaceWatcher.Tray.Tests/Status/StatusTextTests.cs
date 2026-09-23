using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Tests.Status;

public sealed class StatusTextTests
{
    private const long FreeBytes = 44237759283;

    [Fact]
    public void Tooltip_Disconnected_SaysServiceNotRunning() =>
        Assert.Equal("Service not running", StatusText.Tooltip(false, Status(Drive("C", rate: null, eta: null)), null));

    [Fact]
    public void Tooltip_NotLosingFasterThanNoiseFloor_ShowsFreeSpaceOnly() =>
        Assert.Equal("C: 41.2 GB free", StatusText.Tooltip(true, Status(Drive("C", rate: 100_000, eta: TimeSpan.FromDays(5))), null));

    [Fact]
    public void Tooltip_LosingWithEta_ShowsRateAndEta()
    {
        double rate = 1.3 * (1L << 30) / 60;

        string tooltip = StatusText.Tooltip(true, Status(Drive("C", rate, TimeSpan.FromMinutes(31))), null);

        Assert.Equal("C: 41.2 GB free · losing 1.3 GB/min · full in ~31 min", tooltip);
    }

    [Fact]
    public void Tooltip_SeveralDrives_OneLinePerWatchedDrive()
    {
        DriveStatus unwatched = Drive("E", rate: null, eta: null) with { Watched = false };

        string tooltip = StatusText.Tooltip(true, Status(Drive("C", null, null), Drive("D", null, null), unwatched), null);

        Assert.Equal("C: 41.2 GB free\nD: 41.2 GB free", tooltip);
    }

    private static StatusResponse Status(params DriveStatus[] drives) => new(drives, true, null);

    private static DriveStatus Drive(string letter, double? rate, TimeSpan? eta) =>
        new()
        {
            Letter = letter,
            Watched = true,
            Available = true,
            FreeBytes = FreeBytes,
            TotalBytes = FreeBytes * 4,
            DropRateBytesPerSecond = rate,
            TimeToFull = eta,
        };
}
