using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Native;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Turns kernel file and process events into write-aggregator records; runs on the TraceEvent thread only.</summary>
/// <remarks>
/// <para>
/// A create counts as <see cref="WriteKind.Create"/> when its disposition always yields a new or emptied file (create-new,
/// supersede, create-always). Paging-I/O writes (lazy-writer and mapped-page flushes) are dropped: they re-send data a caller
/// already wrote. When the fast-I/O path of a cached write is refused, the kernel logs the attempt (IoFlags 0) and then its
/// IRP retry on the same thread, file object, offset and size; <see cref="FastIoRetryFilter"/> drops the retry so the write
/// counts once.
/// </para>
/// <para>
/// NTFS grows a cached file's end inside the write path, without a SetInformation request, so a file's growth is measured
/// from its writers' own extents: the part of a write that ends past the file's known end is recorded as
/// <see cref="WriteKind.Extend"/> for the writer (<see cref="FileEndTracker"/>). The end-of-file sets (information class 20)
/// that System raises carry the valid data length after each lazy-writer flush, not the file's size, and are ignored. An
/// end-of-file set from any other process (SetEndOfFile, SetLength, mapped-file growth) records its growth over the known end
/// to that process and moves the known end to the new size, so writes after a truncation count as growth again; a set below the
/// known end records the difference as <see cref="WriteKind.Shrink"/>. A delete records the file's known size. The known
/// end is 0 after a creating or truncating open; when it is unknown, the first growth is recorded as 0 and counted in
/// <see cref="ExtendsWithoutBase"/>.
/// </para>
/// </remarks>
/// <param name="aggregators">Supplies the aggregator in use; when it is replaced, the live processes are replayed into the new one.</param>
/// <param name="mapper">Maps the kernel's NT paths to DOS paths.</param>
/// <param name="ownProcessId">The service's pid, whose file events are ignored.</param>
internal sealed class KernelEventRouter(WriteAggregatorProvider aggregators, DevicePathMapper mapper, int ownProcessId)
{
    private const int FileEndOfFileInformation = 20;

    // The System process, whose end-of-file sets report the lazy writer's valid data length rather than the file's size.
    private const int SystemProcessId = 4;

    // IRP_PAGING_IO in wdm.h: set on lazy-writer and mapped-page-writer flushes, which re-send data already counted or
    // run on System threads; mapped-file writers stay visible through their end-of-file growth.
    private const int IrpPagingIo = 0x0002;
    private readonly FileEndTracker _fileEnds = new(FileEndTracker.DefaultCapacity);
    private readonly FastIoRetryFilter _fastIoRetries = new();
    private readonly Dictionary<int, LiveProcess> _live = [];
    private WriteAggregator? _aggregator;
    private long _unmapped;
    private long _pagingWrites;
    private long _fastIoRetriesDropped;

    /// <summary>Gets how many file events were dropped because their path has no drive letter.</summary>
    public long UnmappedEvents => Interlocked.Read(ref _unmapped);

    /// <summary>Gets how many end-of-file growths were recorded as 0 because the file's earlier size was unknown.</summary>
    public long ExtendsWithoutBase => _fileEnds.GrowthsWithoutBase;

    /// <summary>Gets how many paging-I/O write events (cache and mapped-page flushes) were dropped.</summary>
    public long PagingWritesDropped => Interlocked.Read(ref _pagingWrites);

    /// <summary>Gets how many IRP write events were dropped as retries of a fast-I/O write already counted.</summary>
    public long FastIoRetriesDropped => Interlocked.Read(ref _fastIoRetriesDropped);

    /// <summary>Tells whether a write's IRP flags mark it as paging I/O (a cache or mapped-page flush) rather than a caller's write.</summary>
    /// <param name="ioFlags">The event's IoFlags (the IRP flags).</param>
    /// <returns>True when the IRP_PAGING_IO bit is set.</returns>
    internal static bool IsPagingIo(int ioFlags) => (ioFlags & IrpPagingIo) != 0;

    /// <summary>Subscribes to the kernel parser's file and process events, keeping the kernel's NT paths for this router to map.</summary>
    /// <param name="kernel">The session's kernel parser.</param>
    public void Attach(KernelTraceEventParser kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        kernel.KernelPathToUserPathMapper = static path => path;
        kernel.FileIOWrite += OnWrite;
        kernel.FileIOCreate += OnCreate;
        kernel.FileIOSetInfo += OnSetInfo;
        kernel.FileIODelete += OnDelete;
        kernel.ProcessStart += OnProcessStart;
        kernel.ProcessDCStart += OnProcessStart;
        kernel.ProcessStop += OnProcessStop;
    }

    /// <summary>Extracts the executable path from a command line: its quoted first token, or the text up to the first ".exe".</summary>
    /// <param name="commandLine">The process command line.</param>
    /// <returns>The fully qualified executable path, or null when the command line does not start with one.</returns>
    internal static string? ExePathFromCommandLine(string? commandLine)
    {
        string text = (commandLine ?? "").Trim();
        string candidate;
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            candidate = close < 0 ? text[1..] : text[1..close];
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int space = text.IndexOf(' ', StringComparison.Ordinal);
            candidate =
                exe >= 0 ? text[..(exe + 4)]
                : space >= 0 ? text[..space]
                : text;
        }

        return candidate.Length > 0 && Path.IsPathFullyQualified(candidate) ? candidate : null;
    }

    private void OnWrite(FileIOReadWriteTraceData data)
    {
        if (IsPagingIo(data.IoFlags))
        {
            Interlocked.Increment(ref _pagingWrites);
            return;
        }

        if (_fastIoRetries.IsRetry(data.ProcessID, data.ThreadID, data.FileObject, data.Offset, data.IoSize, data.IoFlags))
        {
            Interlocked.Increment(ref _fastIoRetriesDropped);
            return;
        }

        string? path = Record(data.ProcessID, data.TimeStamp, data.FileName, WriteKind.Write, data.IoSize);
        if (path is null)
        {
            return;
        }

        long growth = _fileEnds.Advance(path, data.Offset + data.IoSize);
        if (growth > 0)
        {
            RecordMapped(data.ProcessID, data.TimeStamp, path, WriteKind.Extend, growth);
        }
    }

    private void OnCreate(FileIOCreateTraceData data)
    {
        CreateDisposition disposition = data.CreateDisposition;
        bool createsFile = disposition is CreateDisposition.CREATE_NEW or CreateDisposition.SUPERSEDE or CreateDisposition.CREATE_ALWAYS;
        if (!createsFile && disposition != CreateDisposition.TRUNCATE_EXISTING)
        {
            return;
        }

        string? path = createsFile
            ? Record(data.ProcessID, data.TimeStamp, data.FileName, WriteKind.Create, 0)
            : MapFor(data.ProcessID, data.FileName);
        if (path is not null)
        {
            _fileEnds.Reset(path, 0);
        }
    }

    private void OnSetInfo(FileIOInfoTraceData data)
    {
        if (data.InfoClass == FileEndOfFileInformation)
        {
            SetEndOfFile(data.ProcessID, data.TimeStamp, data.FileName, (long)data.ExtraInfo);
        }
    }

    /// <summary>Records the growth an end-of-file set caused; System's sets are ignored.</summary>
    /// <param name="processId">The process the event was raised in.</param>
    /// <param name="time">The event's time.</param>
    /// <param name="ntPath">The kernel's path for the file.</param>
    /// <param name="newSize">The new end of file the event carries.</param>
    internal void SetEndOfFile(int processId, DateTime time, string ntPath, long newSize)
    {
        if (processId == SystemProcessId)
        {
            return;
        }

        string? path = MapFor(processId, ntPath);
        if (path is null)
        {
            return;
        }

        long? knownEnd = _fileEnds.EndOf(path);
        RecordMapped(processId, time, path, WriteKind.Extend, _fileEnds.Set(path, newSize));
        if (knownEnd is long before && newSize < before)
        {
            RecordMapped(processId, time, path, WriteKind.Shrink, before - newSize);
        }
    }

    /// <summary>Records a deletion with the file's known size, 0 when unknown, and stops tracking the file's end.</summary>
    /// <param name="processId">The process the event was raised in.</param>
    /// <param name="time">The event's time.</param>
    /// <param name="ntPath">The kernel's path for the file.</param>
    internal void Delete(int processId, DateTime time, string ntPath)
    {
        string? path = MapFor(processId, ntPath);
        if (path is null)
        {
            return;
        }

        RecordMapped(processId, time, path, WriteKind.Delete, _fileEnds.EndOf(path) ?? 0);
        _fileEnds.Forget(path);
    }

    private void OnDelete(FileIOInfoTraceData data) => Delete(data.ProcessID, data.TimeStamp, data.FileName);

    private void OnProcessStart(ProcessTraceData data)
    {
        string? exePath = ProcessControl.TryGetImagePath(data.ProcessID) ?? ExePathFromCommandLine(data.CommandLine);
        string image = exePath is not null ? Path.GetFileName(exePath) : data.KernelImageFileName;
        string name = Path.GetFileNameWithoutExtension(image);
        LiveProcess process = new(name.Length > 0 ? name : "pid " + data.ProcessID, exePath, new DateTimeOffset(data.TimeStamp));
        _live[data.ProcessID] = process;
        CurrentAggregator().ProcessStarted(data.ProcessID, process.Name, process.ExePath, process.StartTime);
    }

    private void OnProcessStop(ProcessTraceData data)
    {
        _live.Remove(data.ProcessID);
        _fastIoRetries.ProcessEnded(data.ProcessID);
        CurrentAggregator().ProcessEnded(data.ProcessID, new DateTimeOffset(data.TimeStamp));
    }

    private string? MapFor(int processId, string ntPath)
    {
        if (processId == ownProcessId || string.IsNullOrEmpty(ntPath))
        {
            return null;
        }

        string? path = mapper.ToDosPath(ntPath);
        if (path is null)
        {
            Interlocked.Increment(ref _unmapped);
        }

        return path;
    }

    private string? Record(int processId, DateTime time, string ntPath, WriteKind kind, long bytes)
    {
        string? path = MapFor(processId, ntPath);
        if (path is not null)
        {
            RecordMapped(processId, time, path, kind, bytes);
        }

        return path;
    }

    private void RecordMapped(int processId, DateTime time, string path, WriteKind kind, long bytes) =>
        CurrentAggregator()
            .Record(
                new WriteEvent
                {
                    Time = new DateTimeOffset(time),
                    ProcessId = processId,
                    Drive = char.ToUpperInvariant(path[0]),
                    Path = path,
                    Kind = kind,
                    Bytes = bytes,
                }
            );

    private WriteAggregator CurrentAggregator()
    {
        WriteAggregator current = aggregators.Current;
        if (!ReferenceEquals(current, _aggregator))
        {
            foreach ((int pid, LiveProcess process) in _live)
            {
                current.ProcessStarted(pid, process.Name, process.ExePath, process.StartTime);
            }

            _aggregator = current;
        }

        return current;
    }

    private sealed record LiveProcess(string Name, string? ExePath, DateTimeOffset StartTime);
}
