using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeSpaceWatcher.Tray.Icons;

namespace FreeSpaceWatcher.Tray.Tests.Icons;

public sealed class TrayIconRendererTests
{
    [Theory]
    [InlineData(TrayState.Ok)]
    [InlineData(TrayState.Alert)]
    [InlineData(TrayState.Disconnected)]
    public void Render_DrawsA32PixelGlyphFilledWithTheStateColour(TrayState state)
    {
        Color expected = TrayIconRenderer.FillColor(state);

        RenderResult result = RunOnSta(() => Measure(state));

        Assert.Equal((32, 32), (result.Width, result.Height));
        Assert.Equal((expected.R, expected.G, expected.B, (byte)255), result.CentrePixel);
        Assert.Equal((0, 0, 0, 0), result.CornerPixel);
        Assert.Equal((32, 32), (result.IconWidth, result.IconHeight));
        Assert.Equal((expected.R, expected.G, expected.B), result.IconCentre);
    }

    [Fact]
    public void FillColor_ThreeStates_AreDistinct() =>
        Assert.Equal(3, Enum.GetValues<TrayState>().Select(TrayIconRenderer.FillColor).Distinct().Count());

    private static RenderResult Measure(TrayState state)
    {
        BitmapSource bitmap = TrayIconRenderer.Render(state);
        using var icon = TrayIconRenderer.ToIcon(bitmap);
        using var iconBitmap = icon.ToBitmap();
        System.Drawing.Color iconCentre = iconBitmap.GetPixel(16, 16);
        return new RenderResult(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            Pixel(bitmap, 16, 16),
            Pixel(bitmap, 0, 0),
            icon.Width,
            icon.Height,
            (iconCentre.R, iconCentre.G, iconCentre.B)
        );
    }

    private static (byte R, byte G, byte B, byte A) Pixel(BitmapSource bitmap, int x, int y)
    {
        byte[] bgra = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), bgra, 4, 0);
        return (bgra[2], bgra[1], bgra[0], bgra[3]);
    }

    private static T RunOnSta<T>(Func<T> work)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }

    private sealed record RenderResult(
        int Width,
        int Height,
        (byte R, byte G, byte B, byte A) CentrePixel,
        (byte R, byte G, byte B, byte A) CornerPixel,
        int IconWidth,
        int IconHeight,
        (byte R, byte G, byte B) IconCentre
    );
}
