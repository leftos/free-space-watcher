using System.Text.Json;
using FreeSpaceWatcher.Core.Serialization;
using static System.FormattableString;

namespace FreeSpaceWatcher.Core.Config;

/// <summary>The outcome of loading config.json.</summary>
/// <param name="Config">The configuration in use: the file's contents, or the defaults when the file was missing or unusable.</param>
/// <param name="Error">Why the file could not be used, or null when it loaded (or was missing and the defaults were written).</param>
public sealed record ConfigLoadResult(WatcherConfig Config, string? Error);

/// <summary>Reads and writes the watcher configuration file.</summary>
/// <param name="path">The full path of config.json.</param>
public sealed class ConfigStore(string path)
{
    /// <summary>Loads the configuration, falling back to the defaults when the file is missing or unusable.</summary>
    /// <remarks>
    /// A missing file is created with the defaults. A file that is not valid JSON or fails validation is renamed to
    /// <c>config.json.bad</c> (replacing an older one) and the defaults are returned with the reason in <see cref="ConfigLoadResult.Error"/>.
    /// </remarks>
    /// <param name="fixedDriveLetters">The machine's fixed drives, used to build the defaults.</param>
    /// <returns>The configuration in use and any load error.</returns>
    public ConfigLoadResult Load(IEnumerable<string> fixedDriveLetters)
    {
        ArgumentNullException.ThrowIfNull(fixedDriveLetters);
        if (!File.Exists(path))
        {
            var defaults = WatcherConfig.CreateDefault(fixedDriveLetters);
            Save(defaults);
            return new ConfigLoadResult(defaults, null);
        }

        string? problem = TryRead(out WatcherConfig? config);
        if (problem is null && config is not null)
        {
            return new ConfigLoadResult(config, null);
        }

        string badPath = path + ".bad";
        File.Move(path, badPath, overwrite: true);
        string error = Invariant($"{path}: {problem} The file was renamed to {badPath} and the default settings are in use.");
        return new ConfigLoadResult(WatcherConfig.CreateDefault(fixedDriveLetters), error);
    }

    /// <summary>Validates and writes the configuration, replacing the file atomically.</summary>
    /// <param name="config">The configuration to write.</param>
    /// <exception cref="ArgumentException">The configuration fails validation; the message lists every problem.</exception>
    public void Save(WatcherConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IReadOnlyList<string> errors = ConfigValidator.Validate(config);
        if (errors.Count > 0)
        {
            throw new ArgumentException("The configuration is invalid: " + string.Join(" ", errors), nameof(config));
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, Invariant($"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"));
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(config, CoreJson.Config));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private string? TryRead(out WatcherConfig? config)
    {
        config = null;
        try
        {
            config = JsonSerializer.Deserialize(File.ReadAllText(path), CoreJson.Config);
        }
        catch (JsonException ex)
        {
            return "The file is not valid JSON: " + ex.Message;
        }

        if (config is null)
        {
            return "The file contains null.";
        }

        IReadOnlyList<string> errors = ConfigValidator.Validate(config);
        return errors.Count == 0 ? null : "The file has invalid values: " + string.Join(" ", errors);
    }
}
