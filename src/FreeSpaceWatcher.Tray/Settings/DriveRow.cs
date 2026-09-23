using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>One drive in the settings grid: whether it is watched, its live status, and its threshold overrides.</summary>
/// <param name="letter">The drive letter, e.g. "C".</param>
/// <param name="culture">The culture the override fields use.</param>
public sealed partial class DriveRow(string letter, IFormatProvider culture) : ObservableObject
{
    /// <summary>Gets the drive letter.</summary>
    public string Letter { get; } = letter;

    /// <summary>Gets the drive as shown, e.g. "C:".</summary>
    public string Label => $"{Letter}:";

    /// <summary>Gets or sets whether the drive is watched.</summary>
    [ObservableProperty]
    public partial bool Watched { get; set; }

    /// <summary>Gets the live free space.</summary>
    [ObservableProperty]
    public partial string Free { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the live rate.</summary>
    [ObservableProperty]
    public partial string Rate { get; private set; } = StatusText.Unknown;

    /// <summary>Gets the live time-to-full estimate.</summary>
    [ObservableProperty]
    public partial string Eta { get; private set; } = StatusText.Unknown;

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
    /// <param name="noiseFloorBytesPerMinute">The drive's noise floor.</param>
    public void ApplyStatus(DriveStatus? status, long noiseFloorBytesPerMinute)
    {
        if (status is null)
        {
            Free = StatusText.Unknown;
            Rate = StatusText.Unknown;
            Eta = StatusText.Unknown;
            return;
        }

        Free = status.Available ? ByteFormat.Format(status.FreeBytes) : "not available";
        Rate = StatusText.RateColumn(status.DropRateBytesPerSecond, noiseFloorBytesPerMinute);
        Eta = StatusText.EtaColumn(status, noiseFloorBytesPerMinute);
    }
}
