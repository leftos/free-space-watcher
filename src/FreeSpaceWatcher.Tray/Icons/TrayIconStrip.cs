using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>
/// Renders every tray icon into one PNG for review, for <c>--render-icons &lt;path&gt;</c>: a row per state and taskbar theme, a
/// column per size and fill (0, 50 and 95 %), each icon magnified three times pixel for pixel on a taskbar-coloured cell.
/// </summary>
public static class TrayIconStrip
{
    /// <summary>The tray switch that writes the strip to the path that follows it and exits.</summary>
    public const string Switch = "--render-icons";

    private const int Magnify = 3;
    private const int Cell = 104;
    private const int LabelWidth = 170;
    private const int HeaderHeight = 40;
    private const double Dpi = 96;
    private const double FontSize = 12;

    private static readonly Color DarkTaskbar = Color.FromRgb(0x20, 0x20, 0x20);
    private static readonly Color LightTaskbar = Color.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly Typeface LabelFace = new("Segoe UI");

    /// <summary>Gets the fills shown for each size, as used fractions.</summary>
    public static IReadOnlyList<double> Fills { get; } = [0, 0.5, 0.95];

    /// <summary>Renders the strip and writes it as a PNG.</summary>
    /// <param name="path">The file to write; its folder must exist.</param>
    public static void Save(string path)
    {
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(Render()));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>Renders the strip.</summary>
    /// <returns>A frozen bitmap.</returns>
    public static BitmapSource Render()
    {
        TrayState[] states = Enum.GetValues<TrayState>();
        TaskbarTheme[] themes = [TaskbarTheme.Dark, TaskbarTheme.Light];
        int columns = TrayIconRenderer.Sizes.Count * Fills.Count;
        int width = LabelWidth + (columns * Cell);
        int height = HeaderHeight + (states.Length * themes.Length * Cell);
        DrawingVisual visual = new();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.NearestNeighbor);
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int column = 0; column < columns; column++)
            {
                (int size, double fill) = ColumnOf(column);
                DrawLabel(context, Invariant($"{size} px · {fill * 100:0} %"), LabelWidth + (column * Cell) + 8, 12);
            }

            for (int row = 0; row < states.Length * themes.Length; row++)
            {
                TrayState state = states[row / themes.Length];
                TaskbarTheme theme = themes[row % themes.Length];
                int top = HeaderHeight + (row * Cell);
                DrawLabel(context, Invariant($"{state} · {theme} taskbar"), 8, top + (Cell / 2) - 8);
                for (int column = 0; column < columns; column++)
                {
                    (int size, double fill) = ColumnOf(column);
                    DrawCell(context, TrayIconKey.Create(state, fill, theme), size, new Point(LabelWidth + (column * Cell), top));
                }
            }
        }

        RenderTargetBitmap bitmap = new(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static (int Size, double Fill) ColumnOf(int column) => (TrayIconRenderer.Sizes[column / Fills.Count], Fills[column % Fills.Count]);

    private static void DrawCell(DrawingContext context, TrayIconKey key, int size, Point corner)
    {
        Color background = key.Taskbar == TaskbarTheme.Light ? LightTaskbar : DarkTaskbar;
        context.DrawRectangle(new SolidColorBrush(background), null, new Rect(corner.X + 2, corner.Y + 2, Cell - 4, Cell - 4));
        BitmapSource icon = MagnifyPixels(TrayIconRenderer.Render(key, size), Magnify);
        double offset = (Cell - icon.PixelWidth) / 2;
        context.DrawImage(icon, new Rect(corner.X + offset, corner.Y + offset, icon.PixelWidth, icon.PixelHeight));
    }

    private static void DrawLabel(DrawingContext context, string text, double x, double y) =>
        context.DrawText(
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, FontSize, Brushes.Black, pixelsPerDip: 1),
            new Point(x, y)
        );

    // Each source pixel becomes a factor-by-factor block, so the magnified icon shows the pixels the tray shows.
    private static BitmapSource MagnifyPixels(BitmapSource source, int factor)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        int bigWidth = width * factor;
        int bigHeight = height * factor;
        byte[] big = new byte[bigWidth * bigHeight * 4];
        for (int y = 0; y < bigHeight; y++)
        {
            for (int x = 0; x < bigWidth; x++)
            {
                Array.Copy(pixels, (((y / factor) * width) + (x / factor)) * 4, big, ((y * bigWidth) + x) * 4, 4);
            }
        }

        var magnified = BitmapSource.Create(bigWidth, bigHeight, Dpi, Dpi, PixelFormats.Pbgra32, null, big, bigWidth * 4);
        magnified.Freeze();
        return magnified;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
