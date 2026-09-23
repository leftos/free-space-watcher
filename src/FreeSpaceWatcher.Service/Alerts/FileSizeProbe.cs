namespace FreeSpaceWatcher.Service.Alerts;

/// <summary>Reads file sizes from the file system.</summary>
public sealed class FileSizeProbe : IFileSizeProbe
{
    /// <inheritdoc/>
    public long? SizeOf(string path)
    {
        FileInfo info = new(path);
        return info.Exists ? info.Length : null;
    }
}
