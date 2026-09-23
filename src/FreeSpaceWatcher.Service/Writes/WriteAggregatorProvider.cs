using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Service.Config;

namespace FreeSpaceWatcher.Service.Writes;

/// <summary>Holds the write aggregator built from the configuration, and replaces it when the write window or file cap changes.</summary>
public sealed class WriteAggregatorProvider
{
    private readonly Lock _gate = new();
    private readonly ConfigService _config;
    private (int WindowSeconds, int FileCap) _settings;
    private volatile WriteAggregator _current;

    /// <summary>Builds the aggregator from the current configuration and follows its changes.</summary>
    /// <param name="config">The configuration owner.</param>
    public WriteAggregatorProvider(ConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _settings = SettingsOf(config.Current);
        _current = Create(_settings);
        config.Changed += OnConfigChanged;
    }

    /// <summary>Gets the aggregator in use; a replaced aggregator starts empty.</summary>
    public WriteAggregator Current => _current;

    private static (int WindowSeconds, int FileCap) SettingsOf(WatcherConfig config) => (config.WriteWindowSeconds, config.PerProcessFileCap);

    private static WriteAggregator Create((int WindowSeconds, int FileCap) settings) =>
        new(TimeSpan.FromSeconds(settings.WindowSeconds), settings.FileCap);

    private void OnConfigChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            (int WindowSeconds, int FileCap) settings = SettingsOf(_config.Current);
            if (settings != _settings)
            {
                _settings = settings;
                _current = Create(settings);
            }
        }
    }
}
