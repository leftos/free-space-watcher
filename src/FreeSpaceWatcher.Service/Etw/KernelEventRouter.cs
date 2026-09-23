using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Native;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Turns kernel file and process events into write-aggregator records; runs on the TraceEvent thread only.</summary>
/// <remarks>
/// A create counts as <see cref="WriteKind.Create"/> when its disposition always yields a new or emptied file (create-new,
/// supersede, create-always). An end-of-file set (information class 20) carries the new size, not the growth, so the growth
/// is the new size minus the last size this router saw for the path (0 after such a create or a truncating open); when no
/// earlier size is known the growth is recorded as 0 and counted in <see cref="ExtendsWithoutBase"/>. A growth raised by System
/// (the cache manager) is credited to the path's last caller-writer through <see cref="ExtendAttributor"/>.
/// </remarks>
/// <param name="aggregators">Supplies the aggregator in use; when it is replaced, the live processes are replayed into the new one.</param>
/// <param name="mapper">Maps the kernel's NT paths to DOS paths.</param>
/// <param name="ownProcessId">The service's pid, whose file events are ignored.</param>
internal sealed class KernelEventRouter(WriteAggregatorProvider aggregators, DevicePathMapper mapper, int ownProcessId)
{
    private const int FileEndOfFileInformation = 20;

    // IRP_PAGING_IO in wdm.h: set on lazy-writer and mapped-page-writer flushes, which re-send data already counted or
    // run on System threads; mapped-file writers stay visible through their end-of-file growth.
    private const int IrpPagingIo = 0x0002;
    private const int MaxTrackedSizes = 100_000;
    private readonly Dictionary<string, long> _endOfFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, LiveProcess> _live = [];
    private readonly ExtendAttributor _lastWriters = new(ExtendAttributor.DefaultCapacity);
    private WriteAggregator? _aggregator;
    private long _unmapped;
    private long _extendsWithoutBase;
    private long _pagingWrites;

    /// <summary>Gets how many file events were dropped because their path has no drive letter.</summary>
    public long UnmappedEvents => Interlocked.Read(ref _unmapped);

    /// <summary>Gets how many end-of-file growths were recorded as 0 because the file's earlier size was unknown.</summary>
    public long ExtendsWithoutBase => Interlocked.Read(ref _extendsWithoutBase);

    /// <summary>Gets how many paging-I/O write events (cache and mapped-page flushes) were dropped.</summary>
    public long PagingWritesDropped => Interlocked.Read(ref _pagingWrites);

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

        string? path = Record(data.ProcessID, data.TimeStamp, data.FileName, WriteKind.Write, data.IoSize);
        if (path is not null)
        {
            _lastWriters.Wrote(data.ProcessID, path);
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
            TrackSize(path, 0);
        }
    }

    private void OnSetInfo(FileIOInfoTraceData data)
    {
        if (data.InfoClass != FileEndOfFileInformation)
        {
            return;
        }

        string? path = MapFor(data.ProcessID, data.FileName);
        if (path is null)
        {
            return;
        }

        long newSize = (long)data.ExtraInfo;
        long growth = 0;
        if (_endOfFile.TryGetValue(path, out long previous))
        {
            growth = Math.Max(0, newSize - previous);
        }
        else
        {
            Interlocked.Increment(ref _extendsWithoutBase);
        }

        TrackSize(path, newSize);
        RecordMapped(_lastWriters.CreditExtend(data.ProcessID, path), data.TimeStamp, path, WriteKind.Extend, growth);
    }

    private void OnDelete(FileIOInfoTraceData data)
    {
        string? path = Record(data.ProcessID, data.TimeStamp, data.FileName, WriteKind.Delete, 0);
        if (path is not null)
        {
            _endOfFile.Remove(path);
        }
    }

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
        _lastWriters.ProcessEnded(data.ProcessID);
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

    private void TrackSize(string path, long size)
    {
        if (_endOfFile.Count >= MaxTrackedSizes && !_endOfFile.ContainsKey(path))
        {
            _endOfFile.Clear();
        }

        _endOfFile[path] = size;
    }

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
