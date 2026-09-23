using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>What the tray icon shows.</summary>
public enum TrayState
{
    /// <summary>Connected, no unacknowledged alert.</summary>
    Ok,

    /// <summary>Connected, at least one alert is unacknowledged.</summary>
    Alert,

    /// <summary>The service pipe is not reachable.</summary>
    Disconnected,
}

/// <summary>Draws the tray icons at runtime: a disk glyph filled green, red or grey, so the app ships no image files.</summary>
public static class TrayIconRenderer
{
    /// <summary>The icon's width and height in pixels.</summary>
    public const int SizePixels = 32;

    private const double Dpi = 96;
    private const int IconDirectorySize = 6;
    private const int IconEntrySize = 16;

    /// <summary>Gets the fill colour of the disk glyph for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>Green for OK, red for an unacknowledged alert, grey when the service is unreachable.</returns>
    public static Color FillColor(TrayState state) =>
        state switch
        {
            TrayState.Ok => Color.FromRgb(0x2E, 0x9E, 0x44),
            TrayState.Alert => Color.FromRgb(0xD9, 0x30, 0x25),
            TrayState.Disconnected => Color.FromRgb(0x8A, 0x8A, 0x8A),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown tray state."),
        };

    /// <summary>Draws the disk glyph for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>A frozen <see cref="SizePixels"/>-square bitmap.</returns>
    public static BitmapSource Render(TrayState state)
    {
        Color fill = FillColor(state);
        var edge = Color.FromRgb((byte)(fill.R * 0.6), (byte)(fill.G * 0.6), (byte)(fill.B * 0.6));
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(edge), 2), new Rect(2, 7, 28, 18), 4, 4);
            context.DrawLine(new Pen(Brushes.White, 2), new Point(7, 12), new Point(20, 12));
            context.DrawEllipse(Brushes.White, null, new Point(24, 20), 2, 2);
        }

        RenderTargetBitmap bitmap = new(SizePixels, SizePixels, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Wraps a bitmap in a single-image icon (PNG-compressed), the form the tray icon control takes.</summary>
    /// <param name="bitmap">The bitmap, at most 256 pixels square.</param>
    /// <returns>The icon; the caller disposes it.</returns>
    public static DrawingIcon ToIcon(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream png = new();
        encoder.Save(png);

        using MemoryStream ico = new();
        using (BinaryWriter writer = new(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write((byte)(bitmap.PixelWidth % 256));
            writer.Write((byte)(bitmap.PixelHeight % 256));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)(IconDirectorySize + IconEntrySize));
            writer.Write(png.GetBuffer(), 0, (int)png.Length);
        }

        ico.Position = 0;
        return new DrawingIcon(ico);
    }
}
