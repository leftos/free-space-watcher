namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Turns NT device paths (<c>\Device\HarddiskVolume3\dir\file</c>) into DOS paths (<c>C:\dir\file</c>).</summary>
/// <remarks>
/// The drive table is read for A: to Z: through <c>QueryDosDevice</c>, refreshed every 60 s and on a miss (at most once
/// every 5 s, so a stream of network paths does not re-read it per event). The longest matching device wins.
/// </remarks>
/// <param name="queryDosDevice">Returns the NT device a drive letter points at, or null when the letter is unused.</param>
/// <param name="timeProvider">The clock the refresh intervals are measured on.</param>
public sealed class DevicePathMapper(Func<char, string?> queryDosDevice, TimeProvider timeProvider)
{
    private const string DosDevicesPrefix = @"\??\";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MissRefreshInterval = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private (string Device, char Letter)[] _drives = [];
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    /// <summary>Maps an NT path to its DOS path.</summary>
    /// <param name="ntPath">The kernel's path for a file.</param>
    /// <returns>The DOS path, or null when no drive letter covers the path (network, volume GUID or unnamed paths).</returns>
    public string? ToDosPath(string ntPath)
    {
        ArgumentNullException.ThrowIfNull(ntPath);
        if (IsDosDevicePath(ntPath))
        {
            return ntPath[DosDevicesPrefix.Length..];
        }

        lock (_gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (now - _lastRefresh >= RefreshInterval)
            {
                Refresh(now);
            }

            string? mapped = Match(ntPath);
            if (mapped is null && now - _lastRefresh >= MissRefreshInterval)
            {
                Refresh(now);
                mapped = Match(ntPath);
            }

            return mapped;
        }
    }

    private static bool IsDosDevicePath(string path) =>
        path.Length > DosDevicesPrefix.Length + 2
        && path.StartsWith(DosDevicesPrefix, StringComparison.Ordinal)
        && char.IsAsciiLetter(path[DosDevicesPrefix.Length])
        && path[DosDevicesPrefix.Length + 1] == ':'
        && path[DosDevicesPrefix.Length + 2] == '\\';

    private void Refresh(DateTimeOffset now)
    {
        List<(string Device, char Letter)> drives = [];
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string? device = queryDosDevice(letter);
            if (!string.IsNullOrEmpty(device) && !device.StartsWith(DosDevicesPrefix, StringComparison.Ordinal))
            {
                drives.Add((device.TrimEnd('\\'), letter));
            }
        }

        _drives = [.. drives.OrderByDescending(d => d.Device.Length)];
        _lastRefresh = now;
    }

    private string? Match(string ntPath)
    {
        foreach ((string device, char letter) in _drives)
        {
            if (!ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ntPath.Length == device.Length)
            {
                return letter + @":\";
            }

            if (ntPath[device.Length] == '\\')
            {
                return letter + ":" + ntPath[device.Length..];
            }
        }

        return null;
    }
}
