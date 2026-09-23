using System.Diagnostics;
using System.Security.Principal;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Etw;

/// <summary>Traces file writes and process starts with a private kernel ETW session and feeds them to the write aggregator.</summary>
/// <remarks>
/// On Windows 8 and later TraceEvent starts a session that is not named "NT Kernel Logger" in system-logger mode, so it runs
/// beside other kernel tracers. A stale session of the same name is stopped first. When the session fails or ends while the
/// service runs, it is restarted after 5 s, doubling to 5 min; a session that ran for a minute resets the delay.
/// </remarks>
/// <param name="aggregators">Supplies the aggregator events are recorded into.</param>
/// <param name="mapper">Maps the kernel's NT paths to DOS paths.</param>
/// <param name="logger">Receives session starts, failures and the unmapped-event counts.</param>
/// <param name="options">The session name and the pid whose file events are ignored.</param>
public sealed partial class WriteCollector(
    WriteAggregatorProvider aggregators,
    DevicePathMapper mapper,
    ILogger<WriteCollector> logger,
    WriteCollectorOptions options
) : BackgroundService, IWriteSource
{
    /// <summary>The error reported when the process lacks the rights to start a kernel session.</summary>
    public const string ElevationRequired = "ETW kernel tracing needs administrator or LocalSystem";

    private const KernelTraceEventParser.Keywords SessionKeywords =
        KernelTraceEventParser.Keywords.FileIO
        | KernelTraceEventParser.Keywords.FileIOInit
        | KernelTraceEventParser.Keywords.DiskFileIO
        | KernelTraceEventParser.Keywords.Process;

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StableRun = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CounterLogInterval = TimeSpan.FromMinutes(5);
    private readonly KernelEventRouter _router = new(aggregators, mapper, options.IgnoredProcessId);
    private readonly string _sessionName = options.SessionName;
    private volatile bool _running;
    private volatile string? _error;

    /// <inheritdoc/>
    public bool Running => _running;

    /// <inheritdoc/>
    public string? ErrorMessage => _error;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsElevated())
        {
            _error = ElevationRequired;
            LogNotElevated(logger, ElevationRequired);
            return;
        }

        await Task.WhenAll(RunSessionsAsync(stoppingToken), LogCountersAsync(stoppingToken)).ConfigureAwait(false);
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task RunSessionsAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            long started = Stopwatch.GetTimestamp();
            string failure = await RunOneSessionAsync(stoppingToken).ConfigureAwait(false);
            _running = false;
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            _error = failure;
            if (Stopwatch.GetElapsedTime(started) >= StableRun)
            {
                delay = FirstRetryDelay;
            }

            LogRetrying(logger, failure, delay);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestRetryDelay.Ticks));
        }

        _running = false;
    }

    private async Task<string> RunOneSessionAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task
                .Factory.StartNew(() => RunSession(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);
            return "The ETW session ended unexpectedly; another tool may have stopped it.";
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogSessionFailed(logger, ex);
            return "The ETW session failed: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
            return "The service is stopping.";
        }
    }

    private void RunSession(CancellationToken stoppingToken)
    {
        StopStaleSession();
        using TraceEventSession session = new(_sessionName) { StopOnDispose = true };
        using CancellationTokenRegistration registration = stoppingToken.Register(() => session.Stop(noThrow: true));
        session.EnableKernelProvider(SessionKeywords);
        _router.Attach(session.Source.Kernel);
        _running = true;
        _error = null;
        LogSessionStarted(logger, _sessionName);
        session.Source.Process();
    }

    private void StopStaleSession()
    {
        if (!TraceEventSession.GetActiveSessionNames().Contains(_sessionName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        using var stale = TraceEventSession.GetActiveSession(_sessionName);
        if (stale is not null)
        {
            stale.Stop(noThrow: true);
            LogStaleSessionStopped(logger, _sessionName);
        }
    }

    private async Task LogCountersAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(CounterLogInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                LogCounters(logger, _router.UnmappedEvents, _router.PagingWritesDropped, _router.ExtendsWithoutBase);
            }
        }
        catch (OperationCanceledException)
        {
            LogCounters(logger, _router.UnmappedEvents, _router.PagingWritesDropped, _router.ExtendsWithoutBase);
        }
    }

    [LoggerMessage(
        EventId = 1200,
        Level = LogLevel.Warning,
        Message = "Write tracing is off: {Reason}. Free-space sampling and alerts keep running."
    )]
    private static partial void LogNotElevated(ILogger logger, string reason);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "ETW session {SessionName} started")]
    private static partial void LogSessionStarted(ILogger logger, string sessionName);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "Stopped a stale ETW session named {SessionName}")]
    private static partial void LogStaleSessionStopped(ILogger logger, string sessionName);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning, Message = "The ETW session failed")]
    private static partial void LogSessionFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning, Message = "Write tracing stopped: {Reason} Retrying in {Delay}.")]
    private static partial void LogRetrying(ILogger logger, string reason, TimeSpan delay);

    [LoggerMessage(
        EventId = 1205,
        Level = LogLevel.Debug,
        Message = "File events dropped for paths without a drive letter: {Unmapped}; "
            + "paging-I/O writes dropped (cache and mapped-page flushes): {PagingWrites}; "
            + "end-of-file growths recorded as 0 (earlier size unknown): {ExtendsWithoutBase}"
    )]
    private static partial void LogCounters(ILogger logger, long unmapped, long pagingWrites, long extendsWithoutBase);
}
