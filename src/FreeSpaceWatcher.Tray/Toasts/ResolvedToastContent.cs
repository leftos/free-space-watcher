using FreeSpaceWatcher.Core.Alerts;

namespace FreeSpaceWatcher.Tray.Toasts;

/// <summary>The text of the quiet toast that replaces an alert's toast once the alert resolved itself, built from the alert alone.</summary>
/// <param name="Title">The drive and "resolved", e.g. "C: resolved".</param>
/// <param name="Body">Why the alert resolved, e.g. "pwsh deleted 14.0 GB it had written".</param>
/// <param name="Attribution">The alert toast's own title, e.g. "C: full in ~6 min", so the toast says which alert resolved.</param>
public sealed record ResolvedToastContent(string Title, string Body, string Attribution)
{
    /// <summary>The body when the service gave no reason.</summary>
    public const string NoReason = "Its writers removed what they had written.";

    /// <summary>Builds the resolved toast text of an alert.</summary>
    /// <param name="alert">The resolved alert.</param>
    /// <param name="floorBytes">The drive's floor, for a floor alert's title, or null when the configuration is not known.</param>
    /// <returns>The content.</returns>
    public static ResolvedToastContent From(Alert alert, long? floorBytes)
    {
        ArgumentNullException.ThrowIfNull(alert);
        string reason = string.IsNullOrWhiteSpace(alert.ResolvedReason) ? NoReason : alert.ResolvedReason;
        return new ResolvedToastContent($"{alert.Drive}: resolved", reason, AlertToastContent.From(alert, floorBytes).Title);
    }
}
