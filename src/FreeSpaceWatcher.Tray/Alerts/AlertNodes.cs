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
/// <param name="now">The time the list was loaded, which the relative time counts from.</param>
/// <param name="zone">The time zone times are shown in.</param>
public sealed partial class AlertListItem(AlertSummary summary, DateTimeOffset now, TimeZoneInfo zone) : ObservableObject
{
    /// <summary>Gets the alert summary.</summary>
    public AlertSummary Summary { get; } = summary;

    /// <summary>Gets or sets whether the row is selected; the list binds each row's selection to it.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Gets whether the alert is still unacknowledged; the list marks those with a critical dot and a semibold reason.</summary>
    public bool IsUnacknowledged => !Summary.Acknowledged;

    /// <summary>Gets whether the alert resolved itself; the list marks those with a Resolved pill and secondary text.</summary>
    public bool IsResolved => Summary.ResolvedAt is not null;

    /// <summary>Gets the drive chip's text, e.g. "C:".</summary>
    public string DriveLabel => Summary.Drive + ":";

    /// <summary>Gets the trigger's name, e.g. "Drop rate".</summary>
    public string TriggerName => AlertFormat.TriggerName(Summary.Trigger);

    /// <summary>Gets the one-sentence reason.</summary>
    public string Reason => Summary.Reason;

    /// <summary>Gets how long before the list was loaded the alert was raised, e.g. "2 min ago".</summary>
    public string RelativeTime { get; } = AlertFormat.RelativeTime(summary.Time, now, zone, CultureInfo.CurrentCulture);

    /// <summary>Gets when the alert was raised, in full, for the relative time's tooltip.</summary>
    public string AbsoluteTime { get; } = AlertFormat.AbsoluteTime(summary.Time, zone, CultureInfo.CurrentCulture);
}

/// <summary>An alert's details: the header values and the writers with their folders and files.</summary>
public sealed record AlertDetails
{
    /// <summary>Gets the alert id.</summary>
    public required string Id { get; init; }

    /// <summary>Gets the drive chip's text, e.g. "C:".</summary>
    public required string Drive { get; init; }

    /// <summary>Gets the trigger's name, e.g. "Drop rate".</summary>
    public required string TriggerName { get; init; }

    /// <summary>Gets when the alert was raised, in full.</summary>
    public required string Time { get; init; }

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

    /// <summary>Gets the resolved strip's text, e.g. "Resolved 2 min ago: pwsh deleted 14.0 GB it had written", or null when the alert has not resolved.</summary>
    public required string? ResolvedText { get; init; }

    /// <summary>Gets when the alert resolved, in full, for the resolved strip's tooltip, or null when it has not.</summary>
    public required string? ResolvedTime { get; init; }

    /// <summary>Gets the top writers, most bytes first.</summary>
    public required IReadOnlyList<ProcessNode> Processes { get; init; }

    /// <summary>Gets whether the alert resolved itself.</summary>
    public bool IsResolved => ResolvedText is not null;

    /// <summary>Builds the details of an alert.</summary>
    /// <param name="alert">The alert.</param>
    /// <param name="now">The time the details were loaded, which the resolved time counts from.</param>
    /// <param name="zone">The time zone the alert's times are shown in.</param>
    /// <returns>The details.</returns>
    public static AlertDetails From(Alert alert, DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(alert);
        long topBytes = alert.Processes.Count == 0 ? 0 : alert.Processes.Max(p => p.BytesWritten);
        ProcessNode[] processes = [.. alert.Processes.Select((p, i) => ProcessNode.From(p, topBytes, isTopWriter: i == 0))];
        bool showWriterBar = processes.Length > 1;
        foreach (ProcessNode node in processes)
        {
            node.ShowWriterBar = showWriterBar;
        }

        return new AlertDetails
        {
            Id = alert.Id,
            Drive = alert.Drive + ":",
            TriggerName = AlertFormat.TriggerName(alert.Trigger),
            Time = AlertFormat.AbsoluteTime(alert.Time, zone, CultureInfo.CurrentCulture),
            Reason = alert.Reason,
            Free = ByteFormat.Format(alert.FreeBytes),
            Rate = StatusText.FormatRate(alert.DropRateBytesPerSecond),
            Eta = alert.TimeToFull is TimeSpan eta ? StatusText.FormatEta(eta) : StatusText.Unknown,
            Unattributed = ByteFormat.Format(alert.UnattributedBytes),
            Acknowledged = alert.Acknowledged,
            ResolvedText = ResolvedTextOf(alert, now, zone),
            ResolvedTime = alert.ResolvedAt is DateTimeOffset at ? AlertFormat.AbsoluteTime(at, zone, CultureInfo.CurrentCulture) : null,
            Processes = processes,
        };
    }

    private static string? ResolvedTextOf(Alert alert, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (alert.ResolvedAt is not DateTimeOffset at)
        {
            return null;
        }

        string when = $"Resolved {AlertFormat.RelativeTime(at, now, zone, CultureInfo.CurrentCulture)}";
        return string.IsNullOrWhiteSpace(alert.ResolvedReason) ? when : $"{when}: {alert.ResolvedReason}";
    }
}

/// <summary>A writer's card in the details, with Suspend, Resume and Kill buttons, and its state as the service last reported it.</summary>
/// <param name="report">What the process wrote.</param>
/// <param name="topWriterBytes">The bytes the alert's top writer wrote, which the bar is relative to.</param>
/// <param name="children">The "Folders" and "Files" groups.</param>
public sealed partial class ProcessNode(ProcessWriteReport report, long topWriterBytes, IReadOnlyList<GroupNode> children) : ObservableObject
{
    /// <summary>Gets what the process wrote.</summary>
    public ProcessWriteReport Report { get; } = report;

    /// <summary>Gets the "Folders" and "Files" groups.</summary>
    public IReadOnlyList<GroupNode> Children { get; } = children;

    /// <summary>Gets or sets whether the process is running, suspended or gone; null until the service has said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(CanSuspend), nameof(CanResume), nameof(CanKill))]
    public partial ProcessState? State { get; set; }

    /// <summary>Gets the process id as the card shows it, e.g. "pid 41372".</summary>
    public string PidText => string.Create(CultureInfo.InvariantCulture, $"pid {Report.ProcessId}");

    /// <summary>Gets the executable's full path, or an empty string when it is not known.</summary>
    public string ExePath => Report.ExePath ?? "";

    /// <summary>Gets the executable's path shortened in the middle to <see cref="AlertFormat.PathLength"/> characters, or an empty string.</summary>
    public string ShortExePath => AlertFormat.MiddleTrim(ExePath, AlertFormat.PathLength);

    /// <summary>
    /// Gets the write summary, e.g. "1.0 GB written · 512.0 MB growth · 2 created · 14.0 GB removed"; zero growth, counts and removed
    /// bytes are left out.
    /// </summary>
    public string WriteSummary
    {
        get
        {
            List<string> parts = [$"{ByteFormat.Format(Report.BytesWritten)} written"];
            if (Report.ExtendBytes > 0)
            {
                parts.Add($"{ByteFormat.Format(Report.ExtendBytes)} growth");
            }

            if (Report.FilesCreated > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Report.FilesCreated} created"));
            }

            if (Report.FilesDeleted > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Report.FilesDeleted} deleted"));
            }

            if (Report.RemovedBytes > 0)
            {
                parts.Add($"{ByteFormat.Format(Report.RemovedBytes)} removed");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Gets the bar's length: the bytes written as a fraction of the top writer's.</summary>
    public double BarFraction { get; } = AlertFormat.WriterFraction(report.BytesWritten, topWriterBytes);

    /// <summary>Gets the caption beside the bar, e.g. "62 % of top writer" or "top writer".</summary>
    public string WriterShareText { get; } = AlertFormat.WriterShareText(report.BytesWritten, topWriterBytes);

    /// <summary>Gets or sets whether the bar and its caption are shown; the details hide them for an alert with a single writer.</summary>
    public bool ShowWriterBar { get; set; }

    /// <summary>Gets the state pill's text: "Suspended" or "Exited", or null for a running process or an unknown state.</summary>
    public string? StateText =>
        State switch
        {
            ProcessState.Suspended => "Suspended",
            ProcessState.Exited => "Exited",
            _ => null,
        };

    /// <summary>Gets whether Suspend is offered: not for a process that is already suspended or has exited.</summary>
    public bool CanSuspend => State is not (ProcessState.Suspended or ProcessState.Exited);

    /// <summary>Gets whether Resume is offered: only for a suspended process.</summary>
    public bool CanResume => State == ProcessState.Suspended;

    /// <summary>Gets whether Kill is offered: not for a process that has exited.</summary>
    public bool CanKill => State != ProcessState.Exited;

    /// <summary>Builds the card and its groups.</summary>
    /// <param name="report">What the process wrote.</param>
    /// <param name="topWriterBytes">The bytes the alert's top writer wrote.</param>
    /// <param name="isTopWriter">Whether this is the top writer, whose groups start expanded.</param>
    /// <returns>The card.</returns>
    public static ProcessNode From(ProcessWriteReport report, long topWriterBytes, bool isTopWriter)
    {
        ArgumentNullException.ThrowIfNull(report);
        GroupNode folders = new("Folders", [.. report.Folders.Select(f => new FolderNode(f))], isTopWriter);
        GroupNode files = new("Files", [.. report.Files.Select(f => new FileNode(f))], isTopWriter);
        return new ProcessNode(report, topWriterBytes, [folders, files]);
    }
}

/// <summary>The "Folders" or "Files" expander of a writer's card.</summary>
/// <param name="title">The group name.</param>
/// <param name="children">The folder or file rows.</param>
/// <param name="isExpanded">Whether the expander starts open.</param>
public sealed class GroupNode(string title, IReadOnlyList<object> children, bool isExpanded)
{
    /// <summary>Gets the group name.</summary>
    public string Title { get; } = title;

    /// <summary>Gets the folder or file rows.</summary>
    public IReadOnlyList<object> Children { get; } = children;

    /// <summary>Gets the expander's header, e.g. "Folders (3)".</summary>
    public string Header => string.Create(CultureInfo.InvariantCulture, $"{Title} ({Children.Count})");

    /// <summary>Gets or sets whether the expander is open; the expander writes it back when the user toggles it.</summary>
    public bool IsExpanded { get; set; } = isExpanded;
}

/// <summary>A folder row, with an Open folder button.</summary>
/// <param name="Folder">The folder and the bytes written into it.</param>
public sealed record FolderNode(FolderWrite Folder)
{
    /// <summary>Gets the path shortened in the middle to <see cref="AlertFormat.PathLength"/> characters.</summary>
    public string ShortPath => AlertFormat.MiddleTrim(Folder.Path, AlertFormat.PathLength);

    /// <summary>Gets the bytes written into the folder.</summary>
    public string BytesText => ByteFormat.Format(Folder.Bytes);
}

/// <summary>A file row, with Open folder and Show file buttons.</summary>
/// <param name="File">What the process did to the file.</param>
public sealed record FileNode(FileWrite File)
{
    /// <summary>Gets whether the row stands for the other files of a folder past the per-process cap ("folder\*").</summary>
    public bool IsFolded => File.Path.EndsWith("\\*", StringComparison.Ordinal);

    /// <summary>Gets the folder the file is in.</summary>
    public string FolderPath => Path.GetDirectoryName(IsFolded ? File.Path[..^1] : File.Path) ?? File.Path;

    /// <summary>Gets the path shortened in the middle to <see cref="AlertFormat.PathLength"/> characters.</summary>
    public string ShortPath => AlertFormat.MiddleTrim(File.Path, AlertFormat.PathLength);

    /// <summary>Gets the bytes written, e.g. "1.2 GB written".</summary>
    public string BytesText => $"{ByteFormat.Format(File.BytesWritten)} written";

    /// <summary>Gets the file's current size, e.g. "now 3.4 GB", or an empty string when it is not known.</summary>
    public string SizeText => File.CurrentSize is long size ? $"now {ByteFormat.Format(size)}" : "";
}
