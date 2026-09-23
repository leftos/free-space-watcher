namespace FreeSpaceWatcher.Service;

/// <summary>Where the service keeps its configuration and alert history.</summary>
/// <param name="Root">The data folder: %ProgramData%\FreeSpaceWatcher unless <c>--data-dir</c> overrides it.</param>
public sealed record ServicePaths(string Root)
{
    /// <summary>The command-line option (<c>--data-dir &lt;path&gt;</c>) that overrides the data folder.</summary>
    public const string DataDirOption = "data-dir";

    /// <summary>Gets the full path of config.json.</summary>
    public string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>Gets the folder holding one JSON file per alert.</summary>
    public string AlertsDirectory => Path.Combine(Root, "alerts");

    /// <summary>Resolves the data folder from the <c>--data-dir</c> value, or the ProgramData default when there is none.</summary>
    /// <param name="dataDir">The <c>--data-dir</c> value; a relative path is resolved against the current directory.</param>
    /// <returns>The service's paths.</returns>
    public static ServicePaths Resolve(string? dataDir) =>
        new(
            string.IsNullOrWhiteSpace(dataDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FreeSpaceWatcher")
                : Path.GetFullPath(dataDir)
        );
}
