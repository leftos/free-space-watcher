using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>The settings window: drives with overrides and live status, the defaults, and the advanced timings and counts.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IServiceChannel _channel;
    private readonly Func<IEnumerable<string>> _readyDriveLetters;
    private readonly IFormatProvider _culture;
    private WatcherConfig? _loaded;

    /// <summary>Initializes an empty view model; it loads the configuration once <see cref="IsConnected"/> turns true.</summary>
    /// <param name="channel">The service connection.</param>
    /// <param name="readyDriveLetters">Lists the ready fixed and removable drives, as letters such as "D".</param>
    /// <param name="culture">The culture numbers are typed and shown in.</param>
    public SettingsViewModel(IServiceChannel channel, Func<IEnumerable<string>> readyDriveLetters, IFormatProvider culture)
    {
        _channel = channel;
        _readyDriveLetters = readyDriveLetters;
        _culture = culture;
        DefaultDropRate = Required(InputUnit.GigabytesPerMinute);
        DefaultTimeToFull = Required(InputUnit.Minutes);
        DefaultNoiseFloor = Required(InputUnit.MegabytesPerMinute);
        DefaultFloorBytes = Required(InputUnit.Gigabytes);
        DefaultFloorPercent = Required(InputUnit.Percent);
        DefaultProcessWrite = Required(InputUnit.Gigabytes);
        DefaultGraceDelay = Required(InputUnit.GraceSeconds);
        DefaultResolveWindow = Required(InputUnit.ResolveMinutes);
        SampleInterval = Required(InputUnit.Seconds);
        RateWindow = Required(InputUnit.Seconds);
        WriteWindow = Required(InputUnit.Seconds);
        Cooldown = Required(InputUnit.WholeMinutes);
        HistoryDays = Required(InputUnit.Days);
        TopProcesses = Required(InputUnit.Count);
        TopFolders = Required(InputUnit.Count);
        TopFiles = Required(InputUnit.Count);
        foreach (NumberField field in DefaultFields)
        {
            field.PropertyChanged += OnDefaultFieldChanged;
        }
    }

    /// <summary>Raised when the service accepted the configuration.</summary>
    public event EventHandler? SaveSucceeded;

    /// <summary>Gets the drives: every configured drive plus every ready fixed or removable drive, by letter.</summary>
    public ObservableCollection<DriveRow> Drives { get; } = [];

    /// <summary>Gets the problems to show under the form: invalid fields, service validation errors, lost connections.</summary>
    public ObservableCollection<string> Errors { get; } = [];

    /// <summary>Gets why the service could not use config.json at load time, if it could not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    public partial string? LoadError { get; private set; }

    /// <summary>Gets whether <see cref="LoadError"/> is set.</summary>
    public bool HasLoadError => LoadError is not null;

    /// <summary>Gets or sets whether the service is connected; saving is disabled while it is not.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsConnected { get; set; }

    /// <summary>Gets whether a configuration has been loaded.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Gets whether a load or save is in flight.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the default drop rate (GB/min).</summary>
    public NumberField DefaultDropRate { get; }

    /// <summary>Gets the default time to full (minutes).</summary>
    public NumberField DefaultTimeToFull { get; }

    /// <summary>Gets the default noise floor (MB/min).</summary>
    public NumberField DefaultNoiseFloor { get; }

    /// <summary>Gets the default free-space floor (GB).</summary>
    public NumberField DefaultFloorBytes { get; }

    /// <summary>Gets the default free-space floor (%).</summary>
    public NumberField DefaultFloorPercent { get; }

    /// <summary>Gets the default process write volume (GB).</summary>
    public NumberField DefaultProcessWrite { get; }

    /// <summary>Gets the default grace delay (seconds, 0 = off).</summary>
    public NumberField DefaultGraceDelay { get; }

    /// <summary>Gets the default auto-resolve window (minutes, 0 = off).</summary>
    public NumberField DefaultResolveWindow { get; }

    /// <summary>Gets or sets whether the drop-rate trigger is on by default.</summary>
    [ObservableProperty]
    public partial bool DefaultDropRateEnabled { get; set; }

    /// <summary>Gets or sets whether the time-to-full trigger is on by default.</summary>
    [ObservableProperty]
    public partial bool DefaultTimeToFullEnabled { get; set; }

    /// <summary>Gets or sets whether the floor trigger is on by default.</summary>
    [ObservableProperty]
    public partial bool DefaultFloorEnabled { get; set; }

    /// <summary>Gets or sets whether the process write volume trigger is on by default.</summary>
    [ObservableProperty]
    public partial bool DefaultProcessWriteEnabled { get; set; }

    /// <summary>Gets the sample interval (seconds).</summary>
    public NumberField SampleInterval { get; }

    /// <summary>Gets the rate window (seconds).</summary>
    public NumberField RateWindow { get; }

    /// <summary>Gets the write window (seconds).</summary>
    public NumberField WriteWindow { get; }

    /// <summary>Gets the alert cooldown (minutes).</summary>
    public NumberField Cooldown { get; }

    /// <summary>Gets how many days of alert history are kept.</summary>
    public NumberField HistoryDays { get; }

    /// <summary>Gets how many processes an alert lists.</summary>
    public NumberField TopProcesses { get; }

    /// <summary>Gets how many folders an alert lists per process.</summary>
    public NumberField TopFolders { get; }

    /// <summary>Gets how many files an alert lists per process.</summary>
    public NumberField TopFiles { get; }

    private IReadOnlyList<NumberField> DefaultFields =>
        [
            DefaultDropRate,
            DefaultTimeToFull,
            DefaultNoiseFloor,
            DefaultFloorBytes,
            DefaultFloorPercent,
            DefaultProcessWrite,
            DefaultGraceDelay,
            DefaultResolveWindow,
        ];

    private IReadOnlyList<NumberField> AdvancedFields =>
        [SampleInterval, RateWindow, WriteWindow, Cooldown, HistoryDays, TopProcesses, TopFolders, TopFiles];

    /// <summary>Lists the ready fixed and removable drives of this machine.</summary>
    /// <returns>Their letters, e.g. "C".</returns>
    public static IEnumerable<string> ReadyDriveLetters() =>
        DriveInfo
            .GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady)
            .Select(d => d.Name[..1].ToUpperInvariant());

    /// <summary>Shows a configuration: fills every field and rebuilds the drive list.</summary>
    /// <param name="response">The service's configuration and load error.</param>
    public void Load(ConfigResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        WatcherConfig config = response.Config;
        _loaded = config;
        LoadError = response.LoadError;
        LoadDefaults(config.Defaults);
        SampleInterval.SetValue(config.SampleIntervalSeconds);
        RateWindow.SetValue(config.RateWindowSeconds);
        WriteWindow.SetValue(config.WriteWindowSeconds);
        Cooldown.SetValue(config.CooldownMinutes);
        HistoryDays.SetValue(config.HistoryDays);
        TopProcesses.SetValue(config.TopProcesses);
        TopFolders.SetValue(config.TopFolders);
        TopFiles.SetValue(config.TopFiles);

        Drives.Clear();
        IEnumerable<string> letters = config
            .Drives.Select(d => d.Letter.ToUpperInvariant())
            .Union(_readyDriveLetters().Select(l => l.ToUpperInvariant()))
            .Order(StringComparer.Ordinal);
        foreach (string letter in letters)
        {
            DriveConfig? drive = config.Drives.Find(d => string.Equals(d.Letter, letter, StringComparison.OrdinalIgnoreCase));
            DriveRow row = new(letter, _culture) { Watched = drive?.Enabled ?? false };
            row.Load(drive?.Overrides ?? new Thresholds());
            Drives.Add(row);
        }

        RefreshPlaceholders();
        Errors.Clear();
        IsLoaded = true;
    }

    /// <summary>Builds the configuration the form describes, or lists what is wrong in <see cref="Errors"/>.</summary>
    /// <returns>The configuration, or null when nothing is loaded yet or a field is not accepted.</returns>
    public WatcherConfig? BuildConfig()
    {
        Errors.Clear();
        if (_loaded is null)
        {
            Errors.Add("The configuration has not been loaded from the service yet.");
            return null;
        }

        if (DefaultFields.Concat(AdvancedFields).Concat(Drives.SelectMany(r => r.Fields)).Any(f => f.HasError))
        {
            Errors.Add("Fix the highlighted fields before saving.");
            return null;
        }

        return _loaded with
        {
            Defaults = new ResolvedThresholds
            {
                DropRateBytesPerMinute = WholeValue(DefaultDropRate),
                TimeToFullMinutes = DefaultTimeToFull.Value ?? 0,
                NoiseFloorBytesPerMinute = WholeValue(DefaultNoiseFloor),
                FloorBytes = WholeValue(DefaultFloorBytes),
                FloorPercent = DefaultFloorPercent.Value ?? 0,
                ProcessWriteBytes = WholeValue(DefaultProcessWrite),
                DropRateEnabled = DefaultDropRateEnabled,
                TimeToFullEnabled = DefaultTimeToFullEnabled,
                FloorEnabled = DefaultFloorEnabled,
                ProcessWriteEnabled = DefaultProcessWriteEnabled,
                GraceSeconds = (int)WholeValue(DefaultGraceDelay),
                ResolveMinutes = (int)WholeValue(DefaultResolveWindow),
            },
            Drives = [.. Drives.Select(r => r.ToConfig())],
            SampleIntervalSeconds = (int)WholeValue(SampleInterval),
            RateWindowSeconds = (int)WholeValue(RateWindow),
            WriteWindowSeconds = (int)WholeValue(WriteWindow),
            CooldownMinutes = (int)WholeValue(Cooldown),
            HistoryDays = (int)WholeValue(HistoryDays),
            TopProcesses = (int)WholeValue(TopProcesses),
            TopFolders = (int)WholeValue(TopFolders),
            TopFiles = (int)WholeValue(TopFiles),
        };
    }

    /// <summary>Shows live status on the drive cards.</summary>
    /// <param name="status">The latest status.</param>
    public void ApplyStatus(StatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (DriveRow row in Drives)
        {
            DriveStatus? drive = status.Drives.FirstOrDefault(d => string.Equals(d.Letter, row.Letter, StringComparison.OrdinalIgnoreCase));
            row.ApplyStatus(drive, _loaded?.For(row.Letter) ?? ResolvedThresholds.Default);
        }
    }

    private static long WholeValue(NumberField field) =>
        field.RoundedValue ?? throw new InvalidOperationException($"A required {field.Unit.Label} field is blank.");

    private NumberField Required(InputUnit unit) => new(unit, false, _culture);

    private void LoadDefaults(ResolvedThresholds defaults)
    {
        DefaultDropRate.SetValue(defaults.DropRateBytesPerMinute);
        DefaultTimeToFull.SetValue(defaults.TimeToFullMinutes);
        DefaultNoiseFloor.SetValue(defaults.NoiseFloorBytesPerMinute);
        DefaultFloorBytes.SetValue(defaults.FloorBytes);
        DefaultFloorPercent.SetValue(defaults.FloorPercent);
        DefaultProcessWrite.SetValue(defaults.ProcessWriteBytes);
        DefaultGraceDelay.SetValue(defaults.GraceSeconds);
        DefaultResolveWindow.SetValue(defaults.ResolveMinutes);
        DefaultDropRateEnabled = defaults.DropRateEnabled;
        DefaultTimeToFullEnabled = defaults.TimeToFullEnabled;
        DefaultFloorEnabled = defaults.FloorEnabled;
        DefaultProcessWriteEnabled = defaults.ProcessWriteEnabled;
    }

    private void OnDefaultFieldChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NumberField.Text))
        {
            RefreshPlaceholders();
        }
    }

    private void RefreshPlaceholders()
    {
        foreach (DriveRow row in Drives)
        {
            row.DropRate.Placeholder = DefaultDropRate.Text;
            row.TimeToFull.Placeholder = DefaultTimeToFull.Text;
            row.NoiseFloor.Placeholder = DefaultNoiseFloor.Text;
            row.FloorBytes.Placeholder = DefaultFloorBytes.Text;
            row.FloorPercent.Placeholder = DefaultFloorPercent.Text;
            row.ProcessWrite.Placeholder = DefaultProcessWrite.Text;
            row.GraceDelay.Placeholder = DefaultGraceDelay.Text;
            row.ResolveWindow.Placeholder = DefaultResolveWindow.Text;
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        if (value && !IsLoaded && !IsBusy)
        {
            LoadCommand.Execute(null);
        }
    }

    private bool CanSave() => IsConnected && IsLoaded && !IsBusy;

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            Load(await _channel.SendAsync<ConfigResponse>(new GetConfigRequest(), CancellationToken.None));
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            Errors.Clear();
            Errors.Add($"Could not load the configuration: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        WatcherConfig? config = BuildConfig();
        if (config is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SetConfigResponse response = await _channel.SendAsync<SetConfigResponse>(new SetConfigRequest(config), CancellationToken.None);
            if (response.Ok)
            {
                _loaded = config;
                SaveSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                foreach (string error in response.Errors)
                {
                    Errors.Add(error);
                }
            }
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            Errors.Add($"Could not save the configuration: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
