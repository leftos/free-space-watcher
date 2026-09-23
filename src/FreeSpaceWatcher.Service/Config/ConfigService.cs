using FreeSpaceWatcher.Core.Config;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Config;

/// <summary>Owns config.json: loads it at start, holds the configuration in use, and validates, saves and announces changes.</summary>
public sealed partial class ConfigService
{
    private readonly Lock _gate = new();
    private readonly ConfigStore _store;
    private readonly ILogger<ConfigService> _logger;
    private WatcherConfig _current;

    /// <summary>Loads config.json, creating it with the defaults for this machine's ready fixed and removable drives when missing.</summary>
    /// <param name="paths">Where config.json lives.</param>
    /// <param name="logger">Receives the load outcome; a load error is logged at Warning.</param>
    public ConfigService(ServicePaths paths, ILogger<ConfigService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger;
        _store = new ConfigStore(paths.ConfigFile);
        ConfigLoadResult result = _store.Load(ReadyDriveLetters());
        _current = result.Config;
        LoadError = result.Error;
        if (LoadError is null)
        {
            LogLoaded(_logger, paths.ConfigFile);
        }
        else
        {
            LogLoadError(_logger, LoadError);
        }
    }

    /// <summary>Raised after a new configuration is saved; read <see cref="Current"/> for it.</summary>
    public event EventHandler? Changed;

    /// <summary>Gets the configuration in use.</summary>
    public WatcherConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Gets why config.json could not be used at start, or null when it loaded.</summary>
    public string? LoadError { get; }

    /// <summary>Validates, saves and applies a new configuration.</summary>
    /// <param name="config">The new configuration.</param>
    /// <returns>The validation problems; empty when the configuration was saved and applied.</returns>
    public IReadOnlyList<string> Update(WatcherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IReadOnlyList<string> errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            return errors;
        }

        lock (_gate)
        {
            _store.Save(config);
            _current = config;
        }

        LogUpdated(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
        return [];
    }

    private static string[] ReadyDriveLetters() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady).Select(d => d.Name)];

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration loaded from {Path}")]
    private static partial void LogLoaded(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Configuration could not be loaded: {Error}")]
    private static partial void LogLoadError(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Configuration updated")]
    private static partial void LogUpdated(ILogger logger);
}
