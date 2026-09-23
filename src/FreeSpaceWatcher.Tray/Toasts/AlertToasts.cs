using System.Globalization;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Writes;
using Microsoft.Toolkit.Uwp.Notifications;

namespace FreeSpaceWatcher.Tray.Toasts;

/// <summary>What a click on a toast or one of its buttons asks the tray to do.</summary>
/// <param name="Action">One of the <c>*Action</c> constants.</param>
/// <param name="AlertId">The alert the toast is about, if any.</param>
/// <param name="ProcessId">The process to act on, for <see cref="SuspendAction"/>.</param>
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

/// <summary>Shows the alert toasts: one per pushed alert, or one summary for the unacknowledged alerts found on connect.</summary>
public static class AlertToasts
{
    /// <summary>Shows a toast for a new alert: the reason, the top writer and its top folder, and Details / Suspend / Open folder.</summary>
    /// <param name="alert">The alert.</param>
    public static void ShowAlert(Alert alert) => BuildAlert(alert).Show();

    /// <summary>Shows one toast for the unacknowledged alerts found on connect.</summary>
    /// <param name="count">How many alerts are unacknowledged.</param>
    public static void ShowSummary(int count) =>
        new ToastContentBuilder()
            .AddArgument(ToastRequest.ActionKey, ToastRequest.SummaryAction)
            .AddText(SummaryText(count))
            .AddButton(new ToastButton().SetContent("Details").AddArgument(ToastRequest.ActionKey, ToastRequest.SummaryAction))
            .Show();

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

        ToastButton suspend = new ToastButton()
            .SetContent($"Suspend {top.Name}")
            .AddArgument(ToastRequest.ActionKey, ToastRequest.SuspendAction)
            .AddArgument(ToastRequest.AlertIdKey, alert.Id)
            .AddArgument(ToastRequest.ProcessIdKey, top.ProcessId)
            .AddArgument(ToastRequest.ProcessNameKey, top.Name);
        if (top.StartTime is DateTimeOffset start)
        {
            suspend.AddArgument(ToastRequest.StartTicksKey, start.UtcTicks.ToString(CultureInfo.InvariantCulture));
        }

        builder.AddButton(suspend);
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

    private static ProcessWriteReport? TopWriter(Alert alert) => alert.Processes.Count > 0 ? alert.Processes[0] : null;

    private static string? TopFolder(ProcessWriteReport process) => process.Folders.Count > 0 ? process.Folders[0].Path : null;
}
