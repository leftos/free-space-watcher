namespace FreeSpaceWatcher.Core.Config;

/// <summary>The whole watcher configuration, as stored in config.json.</summary>
public sealed record WatcherConfig
{
    /// <summary>Gets the thresholds every drive uses unless it overrides them.</summary>
    public required ResolvedThresholds Defaults { get; init; }

    /// <summary>Gets the known drives and whether each is watched.</summary>
    public required List<DriveConfig> Drives { get; init; }

    // The members below have setters, not init accessors: the JSON source generator assigns every init-only property of a type with
    // required members in its object initializer, so a config.json that leaves one out would load it as 0 instead of its default.

    /// <summary>Gets or sets the seconds between two free-space samples.</summary>
    public int SampleIntervalSeconds { get; set; } = 1;

    /// <summary>Gets or sets the length, in seconds, of the sample window the drop rate is fitted over.</summary>
    public int RateWindowSeconds { get; set; } = 60;

    /// <summary>Gets or sets the length, in seconds, of the write window.</summary>
    public int WriteWindowSeconds { get; set; } = 300;

    /// <summary>Gets or sets the minimum minutes between two alerts of the same trigger on the same drive.</summary>
    public int CooldownMinutes { get; set; } = 10;

    /// <summary>Gets or sets how many days alerts are kept in history.</summary>
    public int HistoryDays { get; set; } = 30;

    /// <summary>Gets or sets how many distinct files are tracked per process before the rest fold into "other files in a folder".</summary>
    public int PerProcessFileCap { get; set; } = 5000;

    /// <summary>Gets or sets how many processes an alert lists.</summary>
    public int TopProcesses { get; set; } = 5;

    /// <summary>Gets or sets how many folders an alert lists per process.</summary>
    public int TopFolders { get; set; } = 10;

    /// <summary>Gets or sets how many files an alert lists per process.</summary>
    public int TopFiles { get; set; } = 10;

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
