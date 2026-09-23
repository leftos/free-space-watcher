using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>One drive's card in the settings: whether it is watched, its live status, and its threshold overrides.</summary>
/// <remarks>The drive's volume label is read once, when the row is created.</remarks>
/// <param name="letter">The drive letter, e.g. "C".</param>
/// <param name="culture">The culture the override fields use.</param>
public sealed partial class DriveRow(string letter, IFormatProvider culture) : ObservableObject
{
    /// <summary>Gets the drive letter.</summary>
    public string Letter { get; } = letter;

    /// <summary>Gets the drive as shown, e.g. "C:".</summary>
    public string Label => $"{Letter}:";

    /// <summary>Gets the drive's volume label, or an empty string when it has none or it could not be read.</summary>
    public string VolumeLabel { get; } = DriveCardViewModel.ReadVolumeLabel(letter);

    /// <summary>Gets or sets whether the drive is watched.</summary>
    [ObservableProperty]
    public partial bool Watched { get; set; }

    /// <summary>Gets or sets whether the "Custom thresholds" expander is open; it starts open for a drive with an override.</summary>
    [ObservableProperty]
    public partial bool ThresholdsExpanded { get; set; }

    /// <summary>Gets the live free space and size, e.g. "73.8 GB free of 931.5 GB", or "Not available".</summary>
    [ObservableProperty]
    public partial string FreeText { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the used fraction of the drive, from 0 to 1, for the free-space bar.</summary>
    [ObservableProperty]
    public partial double UsedFraction { get; private set; }

    /// <summary>Gets the free-space bar's severity, from the saved thresholds; alerts do not count here.</summary>
    [ObservableProperty]
    public partial DriveSeverity Severity { get; private set; }

    /// <summary>Gets the live rate line, e.g. "Losing 11.2 GB/min · full in ~6 min" or "Steady".</summary>
    [ObservableProperty]
    public partial string RateLine { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the drop-rate override (GB/min).</summary>
    public NumberField DropRate { get; } = new(InputUnit.GigabytesPerMinute, true, culture);

    /// <summary>Gets the time-to-full override (minutes).</summary>
    public NumberField TimeToFull { get; } = new(InputUnit.Minutes, true, culture);

    /// <summary>Gets the noise-floor override (MB/min).</summary>
    public NumberField NoiseFloor { get; } = new(InputUnit.MegabytesPerMinute, true, culture);

    /// <summary>Gets the free-space floor override (GB).</summary>
    public NumberField FloorBytes { get; } = new(InputUnit.Gigabytes, true, culture);

    /// <summary>Gets the free-space floor override (% of the drive).</summary>
    public NumberField FloorPercent { get; } = new(InputUnit.Percent, true, culture);

    /// <summary>Gets the process write volume override (GB).</summary>
    public NumberField ProcessWrite { get; } = new(InputUnit.Gigabytes, true, culture);

    /// <summary>Gets or sets the drop-rate trigger override; null uses the default.</summary>
    [ObservableProperty]
    public partial bool? DropRateEnabled { get; set; }

    /// <summary>Gets or sets the time-to-full trigger override; null uses the default.</summary>
    [ObservableProperty]
    public partial bool? TimeToFullEnabled { get; set; }

    /// <summary>Gets or sets the floor trigger override; null uses the default.</summary>
    [ObservableProperty]
    public partial bool? FloorEnabled { get; set; }

    /// <summary>Gets or sets the process write volume trigger override; null uses the default.</summary>
    [ObservableProperty]
    public partial bool? ProcessWriteEnabled { get; set; }

    /// <summary>Gets every override field.</summary>
    public IReadOnlyList<NumberField> Fields => [DropRate, TimeToFull, NoiseFloor, FloorBytes, FloorPercent, ProcessWrite];

    private IReadOnlyList<bool?> Toggles => [DropRateEnabled, TimeToFullEnabled, FloorEnabled, ProcessWriteEnabled];

    /// <summary>Shows a drive's overrides.</summary>
    /// <param name="overrides">The overrides; blank fields stay blank.</param>
    public void Load(Thresholds overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        DropRate.SetValue(overrides.DropRateBytesPerMinute);
        TimeToFull.SetValue(overrides.TimeToFullMinutes);
        NoiseFloor.SetValue(overrides.NoiseFloorBytesPerMinute);
        FloorBytes.SetValue(overrides.FloorBytes);
        FloorPercent.SetValue(overrides.FloorPercent);
        ProcessWrite.SetValue(overrides.ProcessWriteBytes);
        DropRateEnabled = overrides.DropRateEnabled;
        TimeToFullEnabled = overrides.TimeToFullEnabled;
        FloorEnabled = overrides.FloorEnabled;
        ProcessWriteEnabled = overrides.ProcessWriteEnabled;
        ThresholdsExpanded = Fields.Any(f => !f.IsBlank) || Toggles.Any(t => t is not null);
    }

    /// <summary>Builds the drive's configuration from the row; call only when no field has an error.</summary>
    /// <returns>The drive configuration, blank fields as null overrides.</returns>
    public DriveConfig ToConfig() =>
        new(
            Letter,
            Watched,
            new Thresholds
            {
                DropRateBytesPerMinute = DropRate.RoundedValue,
                TimeToFullMinutes = TimeToFull.Value,
                NoiseFloorBytesPerMinute = NoiseFloor.RoundedValue,
                FloorBytes = FloorBytes.RoundedValue,
                FloorPercent = FloorPercent.Value,
                ProcessWriteBytes = ProcessWrite.RoundedValue,
                DropRateEnabled = DropRateEnabled,
                TimeToFullEnabled = TimeToFullEnabled,
                FloorEnabled = FloorEnabled,
                ProcessWriteEnabled = ProcessWriteEnabled,
            }
        );

    /// <summary>Shows a drive's live status.</summary>
    /// <param name="status">The status, or null when the service reports nothing for the drive.</param>
    /// <param name="thresholds">The drive's saved thresholds, for the noise floor and the bar's severity.</param>
    public void ApplyStatus(DriveStatus? status, ResolvedThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (status is null)
        {
            FreeText = StatusText.Unknown;
            UsedFraction = 0;
            Severity = DriveSeverity.Healthy;
            RateLine = StatusText.Unknown;
            return;
        }

        long noiseFloor = thresholds.NoiseFloorBytesPerMinute;
        bool measured = status.Available && status.TotalBytes > 0;
        FreeText = status.Available ? $"{ByteFormat.Format(status.FreeBytes)} free of {ByteFormat.Format(status.TotalBytes)}" : "Not available";
        UsedFraction = measured ? 1 - ((double)status.FreeBytes / status.TotalBytes) : 0;
        Severity = DriveSeverityRules.For(status, thresholds, hasUnacknowledgedAlert: false);
        RateLine = StatusText.RateLine(status, noiseFloor);
    }
}
