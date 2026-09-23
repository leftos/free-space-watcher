using System.Globalization;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Alerts;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Etw;
using FreeSpaceWatcher.Service.Status;
using FreeSpaceWatcher.Service.Writes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Sampling;

/// <summary>Samples every listed drive's free space each interval, evaluates the watched ones, raises alerts and publishes status.</summary>
/// <remarks>
/// Each watched drive has its own <see cref="TriggerEvaluator"/>, rebuilt when the watched set, the rate window or the cooldown
/// changes. Listed drives that are not watched are sampled for the status only. A drive that is not ready or throws is reported
/// unavailable and its evaluator forgets its samples; the change is logged once per transition.
/// </remarks>
/// <param name="config">Supplies the drives and intervals.</param>
/// <param name="aggregators">Supplies each process's writes to a drive.</param>
/// <param name="alerts">Turns firings into alerts.</param>
/// <param name="hub">Receives the status snapshot every tick.</param>
/// <param name="writes">Reports whether write tracing runs.</param>
/// <param name="logger">Receives drive transitions and tick failures.</param>
public sealed partial class DriveSampler(
    ConfigService config,
    WriteAggregatorProvider aggregators,
    AlertEngine alerts,
    StatusHub hub,
    IWriteSource writes,
    ILogger<DriveSampler> logger
) : BackgroundService
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, TriggerEvaluator> _evaluators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SampleWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _available = new(StringComparer.OrdinalIgnoreCase);
    private string _evaluatorKey = "";

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan retryDelay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            WatcherConfig settings = config.Current;
            var wait = TimeSpan.FromSeconds(settings.SampleIntervalSeconds);
            try
            {
                Tick(settings, DateTimeOffset.Now);
                retryDelay = FirstRetryDelay;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                LogTickFailed(logger, ex, retryDelay);
                wait = retryDelay;
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, LongestRetryDelay.Ticks));
            }

            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Tick(WatcherConfig settings, DateTimeOffset now)
    {
        SyncEvaluators(settings);
        WriteAggregator aggregator = aggregators.Current;
        var writeWindow = TimeSpan.FromSeconds(settings.WriteWindowSeconds);
        List<DriveStatus> statuses = [];
        foreach (DriveConfig drive in settings.Drives)
        {
            statuses.Add(SampleDrive(drive.Letter.ToUpperInvariant(), drive.Enabled, now, aggregator, writeWindow));
        }

        hub.PublishStatus(new StatusResponse(statuses, writes.Running, writes.ErrorMessage));
    }

    private DriveStatus SampleDrive(string letter, bool watched, DateTimeOffset now, WriteAggregator aggregator, TimeSpan writeWindow)
    {
        DriveSample? sample = Read(letter, now, out string? problem);
        _evaluators.TryGetValue(letter, out TriggerEvaluator? evaluator);
        TrackAvailability(letter, watched, problem);
        if (sample is null)
        {
            evaluator?.MarkUnavailable();
            _windows.Remove(letter);
            return Status(letter, watched, null, null);
        }

        if (evaluator is not null)
        {
            SampleWindow window = WindowFor(letter);
            window.Add(sample, writeWindow);
            IReadOnlyList<TriggerFiring> firings = evaluator.Evaluate(sample, aggregator.Totals(letter[0], now));
            alerts.Raise(letter, sample, window.Samples, firings);
        }

        return Status(letter, watched, sample, evaluator);
    }

    private static DriveSample? Read(string letter, DateTimeOffset now, out string? problem)
    {
        try
        {
            DriveInfo info = new(letter);
            if (!info.IsReady)
            {
                problem = "the drive is not ready";
                return null;
            }

            problem = null;
            return new DriveSample(now, info.AvailableFreeSpace, info.TotalSize);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private static DriveStatus Status(string letter, bool watched, DriveSample? sample, TriggerEvaluator? evaluator) =>
        new()
        {
            Letter = letter,
            Watched = watched,
            Available = sample is not null,
            FreeBytes = sample?.FreeBytes ?? 0,
            TotalBytes = sample?.TotalBytes ?? 0,
            DropRateBytesPerSecond = sample is null ? null : evaluator?.CurrentDropRate,
            TimeToFull = sample is null ? null : evaluator?.CurrentTimeToFull,
        };

    private void TrackAvailability(string letter, bool watched, string? problem)
    {
        bool available = problem is null;
        bool known = _available.TryGetValue(letter, out bool previous);
        _available[letter] = available;
        if (!watched || (known && previous == available) || (!known && available))
        {
            return;
        }

        if (available)
        {
            LogDriveBack(logger, letter);
        }
        else
        {
            LogDriveUnavailable(logger, letter, problem ?? "");
        }
    }

    private SampleWindow WindowFor(string letter)
    {
        if (!_windows.TryGetValue(letter, out SampleWindow? window))
        {
            window = new SampleWindow();
            _windows.Add(letter, window);
        }

        return window;
    }

    private void SyncEvaluators(WatcherConfig settings)
    {
        string[] watched = [.. settings.Drives.Where(d => d.Enabled).Select(d => d.Letter.ToUpperInvariant()).Order(StringComparer.Ordinal)];
        string key = string.Create(
            CultureInfo.InvariantCulture,
            $"{string.Join(",", watched)}|{settings.RateWindowSeconds}|{settings.CooldownMinutes}"
        );
        if (key == _evaluatorKey)
        {
            return;
        }

        _evaluatorKey = key;
        _evaluators.Clear();
        _windows.Clear();
        var rateWindow = TimeSpan.FromSeconds(settings.RateWindowSeconds);
        var cooldown = TimeSpan.FromMinutes(settings.CooldownMinutes);
        foreach (string letter in watched)
        {
            _evaluators[letter] = new TriggerEvaluator(letter, () => config.Current.For(letter), rateWindow, cooldown);
        }

        LogWatching(logger, watched.Length == 0 ? "none" : string.Join(", ", watched));
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "Watching drives: {Drives}")]
    private static partial void LogWatching(ILogger logger, string drives);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Drive {Letter}: is unavailable: {Problem}")]
    private static partial void LogDriveUnavailable(ILogger logger, string letter, string problem);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "Drive {Letter}: is available again")]
    private static partial void LogDriveBack(ILogger logger, string letter);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Error, Message = "Sampling failed; retrying in {Delay}")]
    private static partial void LogTickFailed(ILogger logger, Exception exception, TimeSpan delay);
}
