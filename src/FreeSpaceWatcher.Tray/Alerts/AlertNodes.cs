using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>One row of the alert history list.</summary>
/// <param name="summary">The alert summary.</param>
public sealed partial class AlertListItem(AlertSummary summary) : ObservableObject
{
    /// <summary>Gets the alert summary.</summary>
    public AlertSummary Summary { get; } = summary;

    /// <summary>Gets or sets whether the row is selected; the list binds each row's selection to it.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets whether the alert is still unacknowledged; the list shows those in bold.</summary>
    public bool IsUnacknowledged => !Summary.Acknowledged;

    /// <summary>Gets the row text: local time and reason.</summary>
    public string Display => string.Create(CultureInfo.CurrentCulture, $"{Summary.Time.LocalDateTime:g}  {Summary.Reason}");
}

/// <summary>An alert's details: the header values and the process → folders/files tree.</summary>
public sealed record AlertDetails
{
    /// <summary>Gets the alert id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the heading, e.g. "C: DropRate at 14:15".</summary>
    public required string Title { get; init; }

    /// <summary>Gets the one-sentence reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Gets the free space when the alert was raised.</summary>
    public required string Free { get; init; }

    /// <summary>Gets the drop rate when the alert was raised.</summary>
    public required string Rate { get; init; }

    /// <summary>Gets the time-to-full estimate when the alert was raised.</summary>
    public required string Eta { get; init; }

    /// <summary>Gets the part of the drop no traced write explains.</summary>
    public required string Unattributed { get; init; }

    /// <summary>Gets whether the alert is acknowledged.</summary>
    public required bool Acknowledged { get; init; }

    /// <summary>Gets the top writers.</summary>
    public required IReadOnlyList<ProcessNode> Processes { get; init; }

    /// <summary>Builds the details of an alert.</summary>
    /// <param name="alert">The alert.</param>
    /// <returns>The details.</returns>
    public static AlertDetails From(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new AlertDetails
        {
            Id = alert.Id,
            Title = string.Create(CultureInfo.CurrentCulture, $"{alert.Drive}: {alert.Trigger} at {alert.Time.LocalDateTime:g}"),
            Reason = alert.Reason,
            Free = ByteFormat.Format(alert.FreeBytes),
            Rate = StatusText.FormatRate(alert.DropRateBytesPerSecond),
            Eta = alert.TimeToFull is TimeSpan eta ? StatusText.FormatEta(eta) : StatusText.Unknown,
            Unattributed = ByteFormat.Format(alert.UnattributedBytes),
            Acknowledged = alert.Acknowledged,
            Processes = [.. alert.Processes.Select(ProcessNode.From)],
        };
    }
}

/// <summary>A process in the details tree, with Suspend, Resume and Kill buttons, and its state as the service last reported it.</summary>
/// <param name="report">What the process wrote.</param>
/// <param name="children">The "Folders" and "Files" groups.</param>
public sealed partial class ProcessNode(ProcessWriteReport report, IReadOnlyList<GroupNode> children) : ObservableObject
{
    /// <summary>Gets what the process wrote.</summary>
    public ProcessWriteReport Report { get; } = report;

    /// <summary>Gets the "Folders" and "Files" groups.</summary>
    public IReadOnlyList<GroupNode> Children { get; } = children;

    /// <summary>Gets or sets whether the process is running, suspended or gone; null until the service has said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanSuspend), nameof(CanResume))]
    public partial ProcessState? State { get; set; }

    /// <summary>Gets the node text: name, pid, bytes written, created and deleted counts, then " · Suspended" or " · Exited".</summary>
    public string Title =>
        string.Create(
            CultureInfo.CurrentCulture,
            $"{Report.Name} (pid {Report.ProcessId}) — {ByteFormat.Format(Report.BytesWritten)} written, "
                + $"{Report.FilesCreated} created, {Report.FilesDeleted} deleted{StateSuffix}"
        );

    /// <summary>Gets whether Suspend is offered: not for a process that is already suspended or has exited.</summary>
    public bool CanSuspend => State is not (ProcessState.Suspended or ProcessState.Exited);

    /// <summary>Gets whether Resume is offered: only for a suspended process.</summary>
    public bool CanResume => State == ProcessState.Suspended;

    private string StateSuffix =>
        State switch
        {
            ProcessState.Suspended => " · Suspended",
            ProcessState.Exited => " · Exited",
            _ => "",
        };

    /// <summary>Builds the node and its groups.</summary>
    /// <param name="report">What the process wrote.</param>
    /// <returns>The node.</returns>
    public static ProcessNode From(ProcessWriteReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        GroupNode folders = new("Folders", [.. report.Folders.Select(f => new FolderNode(f))]);
        GroupNode files = new("Files", [.. report.Files.Select(f => new FileNode(f))]);
        return new ProcessNode(report, [folders, files]);
    }
}

/// <summary>The "Folders" or "Files" group under a process.</summary>
/// <param name="Title">The group name.</param>
/// <param name="Children">The folder or file nodes.</param>
public sealed record GroupNode(string Title, IReadOnlyList<object> Children);

/// <summary>A folder in the details tree, with an Open folder button.</summary>
/// <param name="Folder">The folder and the bytes written into it.</param>
public sealed record FolderNode(FolderWrite Folder)
{
    /// <summary>Gets the node text: path and bytes.</summary>
    public string Title => $"{Folder.Path} — {ByteFormat.Format(Folder.Bytes)}";
}

/// <summary>A file in the details tree, with Open folder and Show file buttons.</summary>
/// <param name="File">What the process did to the file.</param>
public sealed record FileNode(FileWrite File)
{
    /// <summary>Gets whether the node stands for the other files of a folder past the per-process cap ("folder\*").</summary>
    public bool IsFolded => File.Path.EndsWith("\\*", StringComparison.Ordinal);

    /// <summary>Gets the folder the file is in.</summary>
    public string FolderPath => Path.GetDirectoryName(IsFolded ? File.Path[..^1] : File.Path) ?? File.Path;

    /// <summary>Gets the node text: path, bytes written, current size, and created/deleted flags.</summary>
    public string Title
    {
        get
        {
            string size = File.CurrentSize is long current ? ByteFormat.Format(current) : "unknown";
            string flags = (File.Created, File.Deleted) switch
            {
                (true, true) => ", created and deleted",
                (true, false) => ", created",
                (false, true) => ", deleted",
                _ => "",
            };
            return $"{File.Path} — {ByteFormat.Format(File.BytesWritten)} written, now {size}{flags}";
        }
    }
}
