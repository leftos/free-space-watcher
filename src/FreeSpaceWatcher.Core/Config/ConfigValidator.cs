using static System.FormattableString;

namespace FreeSpaceWatcher.Core.Config;

/// <summary>Checks a configuration for values the watcher cannot work with.</summary>
public static class ConfigValidator
{
    private const int MaxGraceSeconds = 600;
    private const int MaxResolveMinutes = 1440;

    /// <summary>Lists every problem in <paramref name="config"/> as a sentence the settings window can show.</summary>
    /// <param name="config">The configuration to check.</param>
    /// <returns>The problems found; empty when the configuration is usable.</returns>
    public static IReadOnlyList<string> Validate(WatcherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        List<string> errors = [];
        ValidateThresholds("Defaults", ToThresholds(config.Defaults), errors);
        ValidateTiming(config, errors);
        ValidateLimits(config, errors);
        ValidateDrives(config.Drives, errors);
        return errors;
    }

    private static void ValidateThresholds(string scope, Thresholds thresholds, List<string> errors)
    {
        RequirePositive(scope, "drop rate", thresholds.DropRateBytesPerMinute, errors);
        RequirePositive(scope, "time to full", thresholds.TimeToFullMinutes, errors);
        RequirePositive(scope, "noise floor", thresholds.NoiseFloorBytesPerMinute, errors);
        RequirePositive(scope, "floor size", thresholds.FloorBytes, errors);
        RequirePositive(scope, "process write volume", thresholds.ProcessWriteBytes, errors);
        if (thresholds.FloorPercent is double percent && !(percent > 0 && percent <= 100))
        {
            errors.Add(Invariant($"{scope}: floor percent must be greater than 0 and at most 100 (got {percent})."));
        }

        if (thresholds.GraceSeconds is int grace && grace is < 0 or > MaxGraceSeconds)
        {
            errors.Add(Invariant($"{scope}: grace delay must be from 0 to {MaxGraceSeconds} seconds, 0 to disable (got {grace})."));
        }

        if (thresholds.ResolveMinutes is int resolve && resolve is < 0 or > MaxResolveMinutes)
        {
            errors.Add(Invariant($"{scope}: resolve window must be from 0 to {MaxResolveMinutes} minutes, 0 to disable (got {resolve})."));
        }
    }

    private static void RequirePositive(string scope, string name, double? value, List<string> errors)
    {
        if (value is double number && !(number > 0))
        {
            errors.Add(Invariant($"{scope}: {name} must be greater than zero (got {number})."));
        }
    }

    private static void ValidateTiming(WatcherConfig config, List<string> errors)
    {
        if (config.SampleIntervalSeconds < 1)
        {
            errors.Add(Invariant($"Sample interval must be at least 1 second (got {config.SampleIntervalSeconds})."));
        }

        long minimumRateWindow = 5L * config.SampleIntervalSeconds;
        if (config.RateWindowSeconds < minimumRateWindow)
        {
            errors.Add(
                Invariant($"Rate window ({config.RateWindowSeconds} s) must be at least 5 times the sample interval ({minimumRateWindow} s or more).")
            );
        }

        if (config.WriteWindowSeconds < config.RateWindowSeconds)
        {
            errors.Add(
                Invariant($"Write window ({config.WriteWindowSeconds} s) must not be shorter than the rate window ({config.RateWindowSeconds} s).")
            );
        }

        if (config.CooldownMinutes < 0)
        {
            errors.Add(Invariant($"Cooldown must not be negative (got {config.CooldownMinutes})."));
        }
    }

    private static void ValidateLimits(WatcherConfig config, List<string> errors)
    {
        (string Name, int Value)[] minimumOne =
        [
            ("History days", config.HistoryDays),
            ("Per-process file cap", config.PerProcessFileCap),
            ("Top processes", config.TopProcesses),
            ("Top folders", config.TopFolders),
            ("Top files", config.TopFiles),
        ];
        foreach ((string Name, int Value) limit in minimumOne)
        {
            if (limit.Value < 1)
            {
                errors.Add(Invariant($"{limit.Name} must be at least 1 (got {limit.Value})."));
            }
        }
    }

    private static void ValidateDrives(List<DriveConfig> drives, List<string> errors)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DriveConfig drive in drives)
        {
            if (drive is null)
            {
                errors.Add("A drive entry is empty.");
                continue;
            }

            if (!IsDriveLetter(drive.Letter))
            {
                errors.Add(Invariant($"Drive letter '{drive.Letter}' is not a single letter A-Z."));
                continue;
            }

            string letter = drive.Letter.ToUpperInvariant();
            if (!seen.Add(letter))
            {
                errors.Add(Invariant($"Drive {letter} is listed more than once."));
            }

            ValidateThresholds(Invariant($"Drive {letter}"), drive.Overrides, errors);
        }
    }

    private static bool IsDriveLetter(string? letter) => letter is { Length: 1 } && char.IsAsciiLetter(letter[0]);

    private static Thresholds ToThresholds(ResolvedThresholds resolved) =>
        new()
        {
            DropRateBytesPerMinute = resolved.DropRateBytesPerMinute,
            TimeToFullMinutes = resolved.TimeToFullMinutes,
            NoiseFloorBytesPerMinute = resolved.NoiseFloorBytesPerMinute,
            FloorBytes = resolved.FloorBytes,
            FloorPercent = resolved.FloorPercent,
            ProcessWriteBytes = resolved.ProcessWriteBytes,
            GraceSeconds = resolved.GraceSeconds,
            ResolveMinutes = resolved.ResolveMinutes,
        };
}
