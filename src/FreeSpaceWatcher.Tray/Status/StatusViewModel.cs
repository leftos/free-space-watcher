using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreeSpaceWatcher.Core.Config;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Status;

/// <summary>The status window: the connection line, one card per drive (watched first), and the Alerts and Settings buttons.</summary>
/// <remarks>Every member is called on the UI thread.</remarks>
/// <param name="channel">Loads each watched drive's history when its card first appears.</param>
/// <param name="shell">Opens the alerts and settings windows.</param>
/// <param name="log">Receives history requests that failed.</param>
public sealed partial class StatusViewModel(IServiceChannel channel, ITrayShell shell, ITrayLog log) : ObservableObject
{
    private StatusResponse? _lastStatus;

    /// <summary>Gets the drive cards: watched drives first, then the others, each group by letter.</summary>
    public ObservableCollection<DriveCardViewModel> Drives { get; } = [];

    /// <summary>Gets the caption under the title, e.g. "Connected · write tracing on".</summary>
    [ObservableProperty]
    public partial string ConnectionText { get; private set; } = StatusText.ServiceNotRunning;

    /// <summary>Gets the colour of the dot beside <see cref="ConnectionText"/>.</summary>
    [ObservableProperty]
    public partial DriveSeverity ConnectionSeverity { get; private set; } = DriveSeverity.Critical;

    /// <summary>Gets the Alerts button's text: "Alerts", or "Alerts (N new)" while alerts are unacknowledged.</summary>
    [ObservableProperty]
    public partial string AlertsButtonText { get; private set; } = "Alerts";

    /// <summary>Gets whether there are no drive cards to show.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>Shows the tray's current state.</summary>
    /// <param name="connected">Whether the service is connected.</param>
    /// <param name="status">The latest status, if any; a status other than the last one shown is recorded in each drive's history.</param>
    /// <param name="config">The configuration, if loaded, for each drive's floor and noise floor.</param>
    /// <param name="alerts">The alert list, if loaded, for the unacknowledged count and the drives with unacknowledged alerts.</param>
    /// <param name="now">The time a new status is recorded at.</param>
    public void Update(bool connected, StatusResponse? status, WatcherConfig? config, IReadOnlyList<AlertSummary>? alerts, DateTimeOffset now)
    {
        (ConnectionText, ConnectionSeverity) = Connection(connected, status);
        List<AlertSummary> unacknowledged = [.. (alerts ?? []).Where(a => !a.Acknowledged)];
        AlertsButtonText = unacknowledged.Count > 0 ? $"Alerts ({unacknowledged.Count} new)" : "Alerts";
        if (!connected || status is null)
        {
            Drives.Clear();
            _lastStatus = null;
            IsEmpty = true;
            return;
        }

        DateTimeOffset? sampleTime = ReferenceEquals(status, _lastStatus) ? null : now;
        _lastStatus = status;
        List<DriveStatus> ordered = [.. status.Drives.OrderBy(d => d.Watched ? 0 : 1).ThenBy(d => d.Letter, StringComparer.OrdinalIgnoreCase)];
        SyncCards(ordered);
        for (int i = 0; i < ordered.Count; i++)
        {
            DriveStatus drive = ordered[i];
            bool alerting = unacknowledged.Exists(a => string.Equals(a.Drive, drive.Letter, StringComparison.OrdinalIgnoreCase));
            Drives[i].Update(drive, config?.For(drive.Letter) ?? ResolvedThresholds.Default, alerting, sampleTime);
        }

        IsEmpty = Drives.Count == 0;
    }

    private static (string Text, DriveSeverity Severity) Connection(bool connected, StatusResponse? status) =>
        !connected ? (StatusText.ServiceNotRunning, DriveSeverity.Critical)
        : status is null ? ("Connected", DriveSeverity.Healthy)
        : status.EtwRunning ? ("Connected · write tracing on", DriveSeverity.Healthy)
        : ($"Connected · write tracing off: {status.EtwError ?? "no reason given"}", DriveSeverity.Caution);

    // Keeps each drive's card (and its history) across updates, and rebuilds the list only when the drives or their order change.
    private void SyncCards(List<DriveStatus> ordered)
    {
        if (Drives.Select(c => c.Letter).SequenceEqual(ordered.Select(d => d.Letter), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var existing = Drives.ToDictionary(c => c.Letter, StringComparer.OrdinalIgnoreCase);
        Drives.Clear();
        foreach (DriveStatus drive in ordered)
        {
            if (!existing.TryGetValue(drive.Letter, out DriveCardViewModel? card))
            {
                card = new DriveCardViewModel(drive.Letter);
                if (drive.Watched)
                {
                    _ = LoadHistoryAsync(card);
                }
            }

            Drives.Add(card);
        }
    }

    private async Task LoadHistoryAsync(DriveCardViewModel card)
    {
        GetDriveHistoryRequest request = new(card.Letter, (int)SampleHistory.Span.TotalSeconds);
        try
        {
            DriveHistoryResponse response = await channel.SendAsync<DriveHistoryResponse>(request, CancellationToken.None);
            card.MergeHistory(response.Samples, DateTimeOffset.Now);
        }
        catch (Exception ex) when (PipeClient.IsRequestFailure(ex))
        {
            log.Warning($"Loading drive {card.Letter}: history failed; its sparkline starts empty.", ex);
        }
    }

    [RelayCommand]
    private async Task ShowAlertsAsync() => await shell.ShowAlertsAsync(null);

    [RelayCommand]
    private void ShowSettings() => shell.ShowSettings();
}
