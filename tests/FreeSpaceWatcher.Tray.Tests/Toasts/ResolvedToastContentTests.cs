using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Tray.Toasts;
using static FreeSpaceWatcher.Tray.Tests.Toasts.AlertToastsTests;

namespace FreeSpaceWatcher.Tray.Tests.Toasts;

public sealed class ResolvedToastContentTests
{
    private const string Reason = "pwsh deleted 14.0 GB it had written";

    [Fact]
    public void From_TitlesTheDriveResolved_BodyIsTheReason_AttributionIsTheAlertsOwnTitle()
    {
        Alert alert = Resolved(FullAlert("a", "C"), Reason);

        var content = ResolvedToastContent.From(alert, 5L << 30);

        Assert.Equal("C: resolved", content.Title);
        Assert.Equal(Reason, content.Body);
        Assert.Equal("C: below 5.0 GB floor", content.Attribution);
        Assert.Equal(AlertToastContent.From(alert, 5L << 30).Title, content.Attribution);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void From_NoReason_SaysTheWritersRemovedWhatTheyWrote(string? reason)
    {
        var content = ResolvedToastContent.From(Resolved(FullAlert("a", "D"), reason), null);

        Assert.Equal("D: resolved", content.Title);
        Assert.Equal(ResolvedToastContent.NoReason, content.Body);
        Assert.Equal("D: below its floor", content.Attribution);
    }

    [Fact]
    public void BuildResolved_IsSilent_HasTheText_AndOnlyADetailsButtonOnTheAlert()
    {
        string xml = AlertToasts.BuildResolved(Resolved(FullAlert("a-1", "C"), Reason), null).GetToastContent().GetContent();

        Assert.Contains("<audio silent=\"true\"", xml);
        Assert.Contains(">C: resolved<", xml);
        Assert.Contains($">{Reason}<", xml);
        Assert.Contains("placement=\"attribution\"", xml);
        Assert.Single(xml.Split("<action ").Skip(1));
        Assert.Contains("content=\"Details\"", xml);
        Assert.Contains("alertId=a-1", xml);
        Assert.DoesNotContain("<progress", xml);
    }

    private static Alert Resolved(Alert alert, string? reason) => alert with { ResolvedAt = alert.Time.AddMinutes(2), ResolvedReason = reason };
}
