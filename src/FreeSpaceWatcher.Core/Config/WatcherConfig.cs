namespace FreeSpaceWatcher.Core.Config;

/// <summary>The whole watcher configuration, as stored in config.json.</summary>
public sealed record WatcherConfig
{
    /// <summary>Gets the thresholds every drive uses unless it overrides them.</summary>
    public required ResolvedThresholds Defaults { get; init; }

    /// <summary>Gets the known drives and whether each is watched.</summary>
    public required List<DriveConfig> Drives { get; init; }

    /// <summary>Gets the seconds between two free-space samples.</summary>
    public int SampleIntervalSeconds { get; init; } = 1;

    /// <summary>Gets the length, in seconds, of the sample window the drop rate is fitted over.</summary>
    public int RateWindowSeconds { get; init; } = 60;

    /// <summary>Gets the length, in seconds, of the write window.</summary>
    public int WriteWindowSeconds { get; init; } = 300;

    /// <summary>Gets the minimum minutes between two alerts of the same trigger on the same drive.</summary>
    public int CooldownMinutes { get; init; } = 10;

    /// <summary>Gets how many days alerts are kept in history.</summary>
    public int HistoryDays { get; init; } = 30;

    /// <summary>Gets how many distinct files are tracked per process before the rest fold into "other files in a folder".</summary>
    public int PerProcessFileCap { get; init; } = 5000;

    /// <summary>Gets how many processes an alert lists.</summary>
    public int TopProcesses { get; init; } = 5;

    /// <summary>Gets how many folders an alert lists per process.</summary>
    public int TopFolders { get; init; } = 10;

    /// <summary>Gets how many files an alert lists per process.</summary>
    public int TopFiles { get; init; } = 10;

    /// <summary>Builds the default configuration: C: watched, every other fixed drive listed but not watched.</summary>
    /// <param name="fixedDriveLetters">The machine's fixed drives, as "D", "D:" or "D:\".</param>
    /// <returns>The default configuration with the built-in thresholds.</returns>
    public static WatcherConfig CreateDefault(IEnumerable<string> fixedDriveLetters)
    {
        ArgumentNullException.ThrowIfNull(fixedDriveLetters);
        List<DriveConfig> drives = [new DriveConfig("C", true, new Thresholds())];
        foreach (string raw in fixedDriveLetters)
        {
            string letter = NormalizeLetter(raw);
            if (letter.Length == 1 && char.IsAsciiLetter(letter[0]) && !drives.Exists(d => d.Letter == letter))
            {
                drives.Add(new DriveConfig(letter, false, new Thresholds()));
            }
        }

        return new WatcherConfig { Defaults = ResolvedThresholds.Default, Drives = drives };
    }

    /// <summary>Resolves the thresholds for a drive: its overrides over the defaults, or the defaults for an unlisted drive.</summary>
    /// <param name="letter">The drive letter, as "D", "D:" or "D:\".</param>
    /// <returns>The drive's complete thresholds.</returns>
    public ResolvedThresholds For(string letter)
    {
        string normalized = NormalizeLetter(letter);
        DriveConfig? drive = Drives.Find(d => string.Equals(d.Letter, normalized, StringComparison.OrdinalIgnoreCase));
        return drive is null ? Defaults : drive.Overrides.ResolveOver(Defaults);
    }

    internal static string NormalizeLetter(string letter)
    {
        ArgumentNullException.ThrowIfNull(letter);
        return letter.Trim().TrimEnd('\\', ':').ToUpperInvariant();
    }
}
