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

/// <summary>
/// Draws the tray icons at runtime, so the app ships no image files: a horizontal drive body whose usage bar is filled to the used
/// fraction of the fullest watched drive, blue when all is well and red on an alert, with a status dot in the bottom-right corner
/// for an alert (red) or a lost service (grey). Each size is drawn on its own pixel grid rather than scaled.
/// </summary>
/// <remarks>The tray cannot use the WPF theme brushes, so the colours are constants here.</remarks>
public static class TrayIconRenderer
{
    private const double Dpi = 96;
    private const int IconDirectorySize = 6;
    private const int IconEntrySize = 16;

    private static readonly Color OkBlue = Color.FromRgb(0x2B, 0x8A, 0xE8);
    private static readonly Color CriticalRed = Color.FromRgb(0xE5, 0x30, 0x2A);
    private static readonly Color DisconnectedGrey = Color.FromRgb(0x8A, 0x8A, 0x8A);
    private static readonly Color LightOutline = Color.FromRgb(0xF2, 0xF2, 0xF2);
    private static readonly Color DarkOutline = Color.FromRgb(0x1C, 0x1C, 0x1C);

    private static readonly Dictionary<int, IconLayout> Layouts = new()
    {
        [16] = new IconLayout(Stroke: 1, Body: new Int32Rect(0, 3, 16, 10), Radius: 2.5, Gap: 1, Dot: 7),
        [20] = new IconLayout(Stroke: 1, Body: new Int32Rect(0, 4, 20, 12), Radius: 3, Gap: 1, Dot: 9),
        [24] = new IconLayout(Stroke: 2, Body: new Int32Rect(0, 5, 24, 14), Radius: 3.5, Gap: 1, Dot: 10),
        [32] = new IconLayout(Stroke: 2, Body: new Int32Rect(0, 7, 32, 18), Radius: 4.5, Gap: 2, Dot: 14),
    };

    /// <summary>Gets the pixel sizes drawn into every icon: 16, 20, 24 and 32, the tray size at 100 % to 200 % scaling.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 20, 24, 32];

    /// <summary>Gets the colour of the usage bar for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>Blue for OK, red for an unacknowledged alert, grey when the service is unreachable.</returns>
    public static Color BarColor(TrayState state) =>
        state switch
        {
            TrayState.Ok => OkBlue,
            TrayState.Alert => CriticalRed,
            TrayState.Disconnected => DisconnectedGrey,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown tray state."),
        };

    /// <summary>Gets the colour of the status dot for a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>Red for an alert, grey when the service is unreachable, null for OK, which has no dot.</returns>
    public static Color? DotColor(TrayState state) =>
        state switch
        {
            TrayState.Ok => null,
            TrayState.Alert => CriticalRed,
            TrayState.Disconnected => DisconnectedGrey,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown tray state."),
        };

    /// <summary>Gets the colour of the drive outline for a taskbar theme.</summary>
    /// <param name="taskbar">The taskbar theme.</param>
    /// <returns>A dark outline on a light taskbar, a light one on a dark taskbar.</returns>
    public static Color OutlineColor(TaskbarTheme taskbar) => taskbar == TaskbarTheme.Light ? DarkOutline : LightOutline;

    /// <summary>Gets the pixels the usage bar fills when the drive is full.</summary>
    /// <param name="size">One of <see cref="Sizes"/>.</param>
    /// <returns>The bar's rectangle.</returns>
    public static Int32Rect BarBounds(int size)
    {
        IconLayout layout = LayoutFor(size);
        int inset = layout.Stroke + layout.Gap;
        return new Int32Rect(layout.Body.X + inset, layout.Body.Y + inset, layout.Body.Width - (2 * inset), layout.Body.Height - (2 * inset));
    }

    /// <summary>Gets the square the status dot is drawn in, in the bottom-right corner.</summary>
    /// <param name="size">One of <see cref="Sizes"/>.</param>
    /// <returns>The dot's bounding square.</returns>
    public static Int32Rect DotBounds(int size)
    {
        int dot = LayoutFor(size).Dot;
        return new Int32Rect(size - dot, size - dot, dot, dot);
    }

    /// <summary>Gets the pixel size of a small icon at the system DPI, the size the notification area shows.</summary>
    /// <returns>16 at 100 % scaling, 20 at 125 %, 24 at 150 %, 32 at 200 %.</returns>
    public static int SmallIconPixels()
    {
        using var screen = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return (int)Math.Round(SystemParameters.SmallIconWidth * screen.DpiX / Dpi);
    }

    /// <summary>Draws the icon of a key at one size.</summary>
    /// <param name="key">What the icon shows.</param>
    /// <param name="size">One of <see cref="Sizes"/>.</param>
    /// <returns>A frozen <paramref name="size"/>-square bitmap.</returns>
    public static BitmapSource Render(TrayIconKey key, int size)
    {
        IconLayout layout = LayoutFor(size);
        Color? dot = DotColor(key.State);
        Int32Rect dotBounds = DotBounds(size);
        Point dotCentre = new(dotBounds.X + (dotBounds.Width / 2.0), dotBounds.Y + (dotBounds.Height / 2.0));
        double dotRadius = dotBounds.Width / 2.0;
        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            if (dot is not null)
            {
                // A transparent ring around the dot keeps it apart from the drive on any taskbar colour.
                double ring = dotRadius + layout.Stroke;
                context.PushClip(
                    new CombinedGeometry(
                        GeometryCombineMode.Exclude,
                        new RectangleGeometry(new Rect(0, 0, size, size)),
                        new EllipseGeometry(dotCentre, ring, ring)
                    )
                );
            }

            DrawDrive(context, key, size, layout);
            if (dot is Color dotColor)
            {
                context.Pop();
                context.DrawEllipse(new SolidColorBrush(dotColor), null, dotCentre, dotRadius, dotRadius);
            }
        }

        RenderTargetBitmap bitmap = new(size, size, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Draws the icon of a key at every size in <see cref="Sizes"/> and wraps them in one icon.</summary>
    /// <param name="key">What the icon shows.</param>
    /// <param name="preferredSize">The size the icon's handle uses, usually <see cref="SmallIconPixels"/>.</param>
    /// <returns>The icon; the caller disposes it.</returns>
    public static DrawingIcon RenderIcon(TrayIconKey key, int preferredSize) => ToIcon([.. Sizes.Select(size => Render(key, size))], preferredSize);

    /// <summary>Wraps square bitmaps in one multi-size icon (each image PNG-compressed), the form the tray icon control takes.</summary>
    /// <param name="bitmaps">The bitmaps, each at most 256 pixels square.</param>
    /// <param name="preferredSize">The size the icon's handle uses; the closest image is picked.</param>
    /// <returns>The icon; the caller disposes it.</returns>
    public static DrawingIcon ToIcon(IReadOnlyList<BitmapSource> bitmaps, int preferredSize)
    {
        ArgumentNullException.ThrowIfNull(bitmaps);
        List<byte[]> images = [.. bitmaps.Select(EncodePng)];
        using MemoryStream ico = new();
        using (BinaryWriter writer = new(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)images.Count);
            int offset = IconDirectorySize + (IconEntrySize * images.Count);
            for (int i = 0; i < images.Count; i++)
            {
                writer.Write((byte)(bitmaps[i].PixelWidth % 256));
                writer.Write((byte)(bitmaps[i].PixelHeight % 256));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)images[i].Length);
                writer.Write((uint)offset);
                offset += images[i].Length;
            }

            foreach (byte[] image in images)
            {
                writer.Write(image);
            }
        }

        ico.Position = 0;
        return new DrawingIcon(ico, preferredSize, preferredSize);
    }

    private static void DrawDrive(DrawingContext context, TrayIconKey key, int size, IconLayout layout)
    {
        double half = layout.Stroke / 2.0;
        Rect body = new(layout.Body.X + half, layout.Body.Y + half, layout.Body.Width - layout.Stroke, layout.Body.Height - layout.Stroke);
        context.DrawRoundedRectangle(
            null,
            new Pen(new SolidColorBrush(OutlineColor(key.Taskbar)), layout.Stroke),
            body,
            layout.Radius,
            layout.Radius
        );

        Int32Rect bar = BarBounds(size);
        int filled = (int)Math.Round(bar.Width * Math.Clamp(key.FillPercent, 0, 100) / 100.0, MidpointRounding.AwayFromZero);
        if (filled > 0)
        {
            context.DrawRectangle(new SolidColorBrush(BarColor(key.State)), null, new Rect(bar.X, bar.Y, filled, bar.Height));
        }
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream png = new();
        encoder.Save(png);
        return png.ToArray();
    }

    private static IconLayout LayoutFor(int size) =>
        Layouts.TryGetValue(size, out IconLayout? layout)
            ? layout
            : throw new ArgumentOutOfRangeException(nameof(size), size, $"The tray icon is drawn at {string.Join(", ", Sizes)} pixels only.");

    /// <summary>One size's pixel grid.</summary>
    /// <param name="Stroke">The outline's width.</param>
    /// <param name="Body">The drive body's outer bounds.</param>
    /// <param name="Radius">The body's corner radius.</param>
    /// <param name="Gap">The space between the outline and the usage bar.</param>
    /// <param name="Dot">The status dot's diameter.</param>
    private sealed record IconLayout(int Stroke, Int32Rect Body, double Radius, int Gap, int Dot);
}
