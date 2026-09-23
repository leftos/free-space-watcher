namespace FreeSpaceWatcher.Core.Writes;

/// <summary>The kinds of file activity the write collector reports.</summary>
public enum WriteKind
{
    /// <summary>Bytes written to a file.</summary>
    Write,

    /// <summary>A file was created.</summary>
    Create,

    /// <summary>A file was deleted.</summary>
    Delete,

    /// <summary>A file's end grew; the event's bytes are the growth.</summary>
    Extend,
}

/// <summary>One file operation by one process.</summary>
public sealed record WriteEvent
{
    /// <summary>Gets when the operation happened.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Gets the id of the process that performed it.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the drive letter the file is on.</summary>
    public required char Drive { get; init; }

    /// <summary>Gets the file's full DOS path, e.g. C:\foo\bar.log.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the kind of operation.</summary>
    public required WriteKind Kind { get; init; }

    /// <summary>Gets the bytes written, or the growth for <see cref="WriteKind.Extend"/>; ignored for creates and deletes.</summary>
    public required long Bytes { get; init; }
}

/// <summary>What was written to one drive within the write window.</summary>
/// <param name="TotalBytesWritten">Bytes written by every process, not only the listed ones.</param>
/// <param name="TotalExtendBytes">End-of-file growth by every process.</param>
/// <param name="Processes">The top writers, by bytes written descending then process id ascending.</param>
public sealed record DriveWriteSnapshot(long TotalBytesWritten, long TotalExtendBytes, IReadOnlyList<ProcessWriteReport> Processes);

/// <summary>What one process wrote to a drive within the write window.</summary>
public sealed record ProcessWriteReport
{
    /// <summary>Gets the process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the process name, or "pid n" when its start was not seen.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the executable path, when known.</summary>
    public required string? ExePath { get; init; }

    /// <summary>Gets the bytes the process wrote to the drive.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Gets the end-of-file growth the process caused on the drive.</summary>
    public required long ExtendBytes { get; init; }

    /// <summary>Gets how many file creations the process made.</summary>
    public required int FilesCreated { get; init; }

    /// <summary>Gets how many file deletions the process made.</summary>
    public required int FilesDeleted { get; init; }

    /// <summary>Gets the folders written to most, by bytes descending.</summary>
    public required IReadOnlyList<FolderWrite> Folders { get; init; }

    /// <summary>Gets the files written to most, by bytes descending.</summary>
    public required IReadOnlyList<FileWrite> Files { get; init; }
}

/// <summary>Bytes written into one folder: the files directly inside it, rolled up.</summary>
/// <param name="Path">The folder's full path.</param>
/// <param name="Bytes">Bytes written to files directly inside it.</param>
public sealed record FolderWrite(string Path, long Bytes);

/// <summary>What one process did to one file; a path ending in \* stands for the other files in that folder past the per-process cap.</summary>
public sealed record FileWrite
{
    /// <summary>Gets the file's full path, or "folder\*" for the folded other files.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the bytes written to the file.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>Gets the file's end-of-file growth.</summary>
    public required long ExtendBytes { get; init; }

    /// <summary>Gets whether the file was created within the window.</summary>
    public required bool Created { get; init; }

    /// <summary>Gets whether the file was deleted within the window.</summary>
    public required bool Deleted { get; init; }

    /// <summary>Gets the file's size when the alert was raised, or null when it was not measured.</summary>
    public long? CurrentSize { get; init; }
}
