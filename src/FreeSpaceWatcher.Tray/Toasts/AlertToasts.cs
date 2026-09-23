using System.Globalization;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Alerts;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FreeSpaceWatcher.Tray.Toasts;

/// <summary>What a click on a toast or one of its buttons asks the tray to do.</summary>
/// <param name="Action">One of the <c>*Action</c> constants.</param>
/// <param name="AlertId">The alert the toast is about, if any.</param>
/// <param name="ProcessId">The process to act on, for <see cref="SuspendAction"/> and <see cref="ResumeAction"/>.</param>
/// <param name="ProcessStartTime">The process start time, when known.</param>
/// <param name="ProcessName">The process name, for messages.</param>
/// <param name="Folder">The folder to open, for <see cref="OpenFolderAction"/>.</param>
public sealed record ToastRequest(
    string Action,
    string? AlertId,
    int? ProcessId,
    DateTimeOffset? ProcessStartTime,
    string? ProcessName,
    string? Folder
)
{
    /// <summary>Open the alerts window on the toast's alert.</summary>
    public const string DetailsAction = "details";

    /// <summary>Open the alerts window on the whole history.</summary>
    public const string SummaryAction = "summary";

    /// <summary>Suspend the top writer.</summary>
    public const string SuspendAction = "suspend";

    /// <summary>Resume the process a confirmation toast is about.</summary>
    public const string ResumeAction = "resume";

    /// <summary>Open the top writer's top folder.</summary>
    public const string OpenFolderAction = "openFolder";

    internal const string ActionKey = "action";
    internal const string AlertIdKey = "alertId";
    internal const string ProcessIdKey = "pid";
    internal const string StartTicksKey = "startTicks";
    internal const string ProcessNameKey = "name";
    internal const string FolderKey = "folder";

    /// <summary>Reads the arguments a toast activation carries.</summary>
    /// <param name="argument">The activation argument string.</param>
    /// <returns>The request; a missing action reads as <see cref="SummaryAction"/>.</returns>
    public static ToastRequest Parse(string argument)
    {
        var args = ToastArguments.Parse(argument ?? "");
        int? processId = args.TryGetValue(ProcessIdKey, out string pid) && int.TryParse(pid, CultureInfo.InvariantCulture, out int p) ? p : null;
        DateTimeOffset? start =
            args.TryGetValue(StartTicksKey, out string ticks) && long.TryParse(ticks, CultureInfo.InvariantCulture, out long t)
                ? new DateTimeOffset(t, TimeSpan.Zero)
                : null;
        return new ToastRequest(
            args.TryGetValue(ActionKey, out string action) ? action : SummaryAction,
            args.TryGetValue(AlertIdKey, out string alertId) ? alertId : null,
            processId,
            start,
            args.TryGetValue(ProcessNameKey, out string name) ? name : null,
            args.TryGetValue(FolderKey, out string folder) ? folder : null
        );
    }
}

/// <summary>The outcome of a process action started from a toast, for the toast that reports it.</summary>
public sealed record ProcessActionToast
{
    /// <summary>Gets the action.</summary>
    public required ProcessAction Action { get; init; }

    /// <summary>Gets the process id.</summary>
    public required int ProcessId { get; init; }

    /// <summary>Gets the process start time, when known.</summary>
    public required DateTimeOffset? ProcessStartTime { get; init; }

    /// <summary>Gets the process name.</summary>
    public required string ProcessName { get; init; }

    /// <summary>Gets the alert the action came from, if any.</summary>
    public required string? AlertId { get; init; }

    /// <summary>Gets why the action failed, or null when it succeeded.</summary>
    public required string? Error { get; init; }
}

/// <summary>Where a toast sits in Action Center: a newer toast with the same tag and group replaces it, and removing the group removes it.</summary>
/// <param name="Tag">The toast's tag.</param>
/// <param name="Group">The toast's group.</param>
public sealed record ToastSlot(string Tag, string Group);

/// <summary>
/// Shows the toasts: one per pushed alert, replacing the previous one for the same drive; one summary for the unacknowledged
/// alerts found on connect, replacing the previous summary; and one per process action started from a toast, replacing the
/// previous one for the same process. Removes alert toasts from Action Center once their alerts are acknowledged or deleted.
/// </summary>
public static class AlertToasts
{
    /// <summary>The toast group of alert toasts, whose tag is the drive letter, and of the summary toast, tagged <see cref="SummaryTag"/>.</summary>
    public const string AlertsGroup = "alerts";

    /// <summary>The toast group of process action toasts, whose tag is "action-" and the process id.</summary>
    public const string ActionsGroup = "actions";

    /// <summary>The summary toast's tag; no drive letter is this long, so it never replaces an alert toast.</summary>
    public const string SummaryTag = "summary";

    /// <summary>Gets the summary toast's slot: in <see cref="AlertsGroup"/>, so clearing the alert toasts clears it too.</summary>
    public static ToastSlot SummarySlot { get; } = new(SummaryTag, AlertsGroup);

    /// <summary>Gets an alert toast's slot: tagged with the alert's drive letter, in <see cref="AlertsGroup"/>.</summary>
    /// <param name="alert">The alert.</param>
    /// <returns>The slot.</returns>
    public static ToastSlot AlertSlot(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        return new ToastSlot(alert.Drive, AlertsGroup);
    }

    /// <summary>Gets a process action toast's slot: tagged "action-" and the process id, in <see cref="ActionsGroup"/>.</summary>
    /// <param name="processId">The process id.</param>
    /// <returns>The slot.</returns>
    public static ToastSlot ProcessActionSlot(int processId) => new(string.Create(CultureInfo.InvariantCulture, $"action-{processId}"), ActionsGroup);

    /// <summary>Shows a toast for a new alert: the reason, the top writer and its top folder, and Details / Suspend / Open folder.</summary>
    /// <param name="alert">The alert.</param>
    public static void ShowAlert(Alert alert) => Show(BuildAlert(alert), AlertSlot(alert));

    /// <summary>
    /// Shows the outcome of a process action started from a toast: "Suspended pwsh (pid 41372)" with Resume and Details, or the
    /// error with Details.
    /// </summary>
    /// <param name="outcome">The outcome.</param>
    public static void ShowProcessAction(ProcessActionToast outcome) => Show(BuildProcessAction(outcome), ProcessActionSlot(outcome.ProcessId));

    /// <summary>Shows one toast for the unacknowledged alerts found on connect, replacing the previous one.</summary>
    /// <param name="count">How many alerts are unacknowledged.</param>
    public static void ShowSummary(int count) =>
        Show(
            new ToastContentBuilder()
                .AddArgument(ToastRequest.ActionKey, ToastRequest.SummaryAction)
                .AddText(SummaryText(count))
                .AddButton(new ToastButton().SetContent("Details").AddArgument(ToastRequest.ActionKey, ToastRequest.SummaryAction)),
            SummarySlot
        );

    /// <summary>Removes every alert toast, and the summary toast, from Action Center.</summary>
    public static void RemoveAll() => ToastNotificationManagerCompat.History.RemoveGroup(AlertsGroup);

    /// <summary>Removes one drive's alert toast from Action Center.</summary>
    /// <param name="drive">The drive letter the toast is tagged with.</param>
    public static void RemoveForDrive(string drive) => ToastNotificationManagerCompat.History.Remove(drive, AlertsGroup);

    /// <summary>
    /// Picks the drives whose alert toast goes after a change: each drive the alerts named before the change that has no
    /// unacknowledged alert after it, whether its alerts were acknowledged or deleted.
    /// </summary>
    /// <param name="before">The alerts before the change.</param>
    /// <param name="after">The alerts after the change.</param>
    /// <returns>The drive letters, once each, as the alerts before the change name them.</returns>
    public static IReadOnlyList<string> DrivesToClear(IReadOnlyList<AlertSummary> before, IReadOnlyList<AlertSummary> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        HashSet<string> alertingDrives = new(after.Where(a => !a.Acknowledged).Select(a => a.Drive), StringComparer.OrdinalIgnoreCase);
        return [.. before.Select(a => a.Drive).Distinct(StringComparer.OrdinalIgnoreCase).Where(drive => !alertingDrives.Contains(drive))];
    }

    /// <summary>Formats the summary toast's text, e.g. "3 unacknowledged disk alerts".</summary>
    /// <param name="count">How many alerts are unacknowledged.</param>
    /// <returns>The text.</returns>
    public static string SummaryText(int count) =>
        count == 1 ? "1 unacknowledged disk alert" : string.Create(CultureInfo.CurrentCulture, $"{count} unacknowledged disk alerts");

    /// <summary>Formats an alert toast's body: the top writer, its bytes written and its top folder.</summary>
    /// <param name="alert">The alert.</param>
    /// <returns>The body text.</returns>
    public static string Body(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        if (TopWriter(alert) is not { } top)
        {
            return $"No traced writer; {ByteFormat.Format(alert.UnattributedBytes)} unattributed";
        }

        string written = $"{top.Name} wrote {ByteFormat.Format(top.BytesWritten)}";
        return TopFolder(top) is string folder ? $"{written} in {folder}" : written;
    }

    private static ToastContentBuilder BuildAlert(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ToastContentBuilder builder = new ToastContentBuilder()
            .AddArgument(ToastRequest.ActionKey, ToastRequest.DetailsAction)
            .AddArgument(ToastRequest.AlertIdKey, alert.Id)
            .AddText(alert.Reason)
            .AddText(Body(alert))
            .AddButton(
                new ToastButton()
                    .SetContent("Details")
                    .AddArgument(ToastRequest.ActionKey, ToastRequest.DetailsAction)
                    .AddArgument(ToastRequest.AlertIdKey, alert.Id)
            );
        if (TopWriter(alert) is not { } top)
        {
            return builder;
        }

        builder.AddButton(ProcessButton($"Suspend {top.Name}", ToastRequest.SuspendAction, alert.Id, (top.ProcessId, top.Name, top.StartTime)));
        if (TopFolder(top) is string folder)
        {
            builder.AddButton(
                new ToastButton()
                    .SetContent("Open folder")
                    .AddArgument(ToastRequest.ActionKey, ToastRequest.OpenFolderAction)
                    .AddArgument(ToastRequest.FolderKey, folder)
            );
        }

        return builder;
    }

    private static ToastContentBuilder BuildProcessAction(ProcessActionToast outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ToastContentBuilder builder = new ToastContentBuilder().AddArgument(ToastRequest.ActionKey, ToastRequest.DetailsAction);
        ToastButton details = new ToastButton().SetContent("Details").AddArgument(ToastRequest.ActionKey, ToastRequest.DetailsAction);
        if (outcome.AlertId is string alertId)
        {
            builder.AddArgument(ToastRequest.AlertIdKey, alertId);
            details.AddArgument(ToastRequest.AlertIdKey, alertId);
        }

        if (outcome.Error is string error)
        {
            return builder
                .AddText(ProcessActionText.Failed(outcome.Action, outcome.ProcessName, outcome.ProcessId))
                .AddText(error)
                .AddButton(details);
        }

        builder.AddText(ProcessActionText.Succeeded(outcome.Action, outcome.ProcessName, outcome.ProcessId));
        if (outcome.Action == ProcessAction.Suspend)
        {
            builder
                .AddText(ProcessActionText.SuspendedBody)
                .AddButton(
                    ProcessButton(
                        "Resume",
                        ToastRequest.ResumeAction,
                        outcome.AlertId,
                        (outcome.ProcessId, outcome.ProcessName, outcome.ProcessStartTime)
                    )
                );
        }

        return builder.AddButton(details);
    }

    private static ToastButton ProcessButton(string content, string action, string? alertId, (int Id, string Name, DateTimeOffset? StartTime) process)
    {
        ToastButton button = new ToastButton()
            .SetContent(content)
            .AddArgument(ToastRequest.ActionKey, action)
            .AddArgument(ToastRequest.ProcessIdKey, process.Id)
            .AddArgument(ToastRequest.ProcessNameKey, process.Name);
        if (alertId is not null)
        {
            button.AddArgument(ToastRequest.AlertIdKey, alertId);
        }

        if (process.StartTime is DateTimeOffset start)
        {
            button.AddArgument(ToastRequest.StartTicksKey, start.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        return button;
    }

    private static void Show(ToastContentBuilder builder, ToastSlot slot) =>
        builder.Show(toast =>
        {
            toast.Tag = slot.Tag;
            toast.Group = slot.Group;
        });

    private static ProcessWriteReport? TopWriter(Alert alert) => alert.Processes.Count > 0 ? alert.Processes[0] : null;

    private static string? TopFolder(ProcessWriteReport process) => process.Folders.Count > 0 ? process.Folders[0].Path : null;
}
