namespace FreeSpaceWatcher.Service.Alerts;

/// <summary>Reads a file's current size.</summary>
public interface IFileSizeProbe
{
    /// <summary>Returns a file's size.</summary>
    /// <param name="path">The file's full path.</param>
    /// <returns>The size in bytes, or null when the file does not exist.</returns>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The service may not read the file.</exception>
    long? SizeOf(string path);
}
