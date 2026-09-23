using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>Starts the processes the alerts window and the toasts need: Explorer, and the elevation helper.</summary>
public interface IShellActions
{
    /// <summary>Opens a folder in Explorer.</summary>
    /// <param name="folder">The folder.</param>
    /// <returns>Why Explorer could not be started, or null.</returns>
    string? OpenFolder(string folder);

    /// <summary>Opens a file's folder in Explorer with the file selected.</summary>
    /// <param name="file">The file.</param>
    /// <returns>Why Explorer could not be started, or null.</returns>
    string? ShowFile(string file);

    /// <summary>Runs the elevation helper through a UAC prompt to act on a process the user's token cannot touch.</summary>
    /// <param name="action">The action.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="processStartTime">The process start time, when known.</param>
    /// <returns>Why the helper could not be started, or null when it started or the user cancelled the prompt.</returns>
    string? RunElevated(ProcessAction action, int processId, DateTimeOffset? processStartTime);
}

/// <summary>Starts Explorer and the elevation helper with argument lists, never through a command string.</summary>
/// <param name="elevateHelperPath">The full path of FreeSpaceWatcher.Elevate.exe.</param>
public sealed class ShellActions(string elevateHelperPath) : IShellActions
{
    /// <summary>The elevation helper's file name; it ships next to the tray executable.</summary>
    public const string ElevateHelperFileName = "FreeSpaceWatcher.Elevate.exe";

    private const int ErrorCancelled = 1223;

    /// <summary>Gets the helper's expected path: next to the tray executable.</summary>
    public static string DefaultElevateHelperPath => Path.Combine(AppContext.BaseDirectory, ElevateHelperFileName);

    private static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Builds the start info for the helper: runas verb, shell execute, args "suspend|resume|kill pid [startTimeUtcTicks]".</summary>
    /// <param name="helperPath">The helper's path.</param>
    /// <param name="action">The action.</param>
    /// <param name="processId">The process id.</param>
    /// <param name="processStartTime">The process start time; left out when unknown.</param>
    /// <returns>The start info.</returns>
    public static ProcessStartInfo CreateElevatedStartInfo(string helperPath, ProcessAction action, int processId, DateTimeOffset? processStartTime)
    {
        ProcessStartInfo info = new(helperPath) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add(ActionArgument(action));
        info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        if (processStartTime is DateTimeOffset start)
        {
            info.ArgumentList.Add(start.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        return info;
    }

    /// <inheritdoc/>
    public string? OpenFolder(string folder)
    {
        ProcessStartInfo info = new(ExplorerPath);
        info.ArgumentList.Add(folder);
        return Launch(info);
    }

    /// <inheritdoc/>
    public string? ShowFile(string file)
    {
        ProcessStartInfo info = new(ExplorerPath);
        info.ArgumentList.Add("/select,");
        info.ArgumentList.Add(file);
        return Launch(info);
    }

    /// <inheritdoc/>
    public string? RunElevated(ProcessAction action, int processId, DateTimeOffset? processStartTime)
    {
        if (!File.Exists(elevateHelperPath))
        {
            return $"The elevation helper is missing. It is expected at {elevateHelperPath}.";
        }

        try
        {
            using var process = Process.Start(CreateElevatedStartInfo(elevateHelperPath, action, processId, processStartTime));
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Trace.TraceInformation($"The UAC prompt for {elevateHelperPath} was cancelled.");
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"Could not start {elevateHelperPath}: {ex.Message}";
        }
    }

    private static string ActionArgument(ProcessAction action) =>
        action switch
        {
            ProcessAction.Suspend => "suspend",
            ProcessAction.Resume => "resume",
            ProcessAction.Kill => "kill",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown process action."),
        };

    private static string? Launch(ProcessStartInfo info)
    {
        try
        {
            using var process = Process.Start(info);
            return null;
        }
        catch (Win32Exception ex)
        {
            return $"Could not start {info.FileName}: {ex.Message}";
        }
    }
}
