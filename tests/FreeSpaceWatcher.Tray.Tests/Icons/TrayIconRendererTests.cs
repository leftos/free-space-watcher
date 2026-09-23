using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FreeSpaceWatcher.Tray.Icons;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Tests.Icons;

public sealed class TrayIconRendererTests
{
    private static readonly (byte R, byte G, byte B, byte A) Transparent = (0, 0, 0, 0);

    public static TheoryData<int> Sizes => [.. TrayIconRenderer.Sizes];

    public static TheoryData<int, TrayState> SizesAndBarStates =>
        [.. TrayIconRenderer.Sizes.SelectMany(size => new[] { (size, TrayState.Ok), (size, TrayState.Alert) })];

    public static TheoryData<int, TrayState> SizesAndStates =>
        [.. TrayIconRenderer.Sizes.SelectMany(size => Enum.GetValues<TrayState>().Select(state => (size, state)))];

    public static TheoryData<int, TaskbarTheme> SizesAndThemes =>
        [.. TrayIconRenderer.Sizes.SelectMany(size => new[] { (size, TaskbarTheme.Dark), (size, TaskbarTheme.Light) })];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Render_EachSize_IsThatManyPixelsSquare(int size)
    {
        (int Width, int Height) dimensions = RunOnSta(() =>
        {
            BitmapSource bitmap = TrayIconRenderer.Render(new TrayIconKey(TrayState.Ok, 50, TaskbarTheme.Dark), size);
            return (bitmap.PixelWidth, bitmap.PixelHeight);
        });

        Assert.Equal((size, size), dimensions);
    }

    [Theory]
    [MemberData(nameof(SizesAndBarStates))]
    public void Render_EmptyDrive_LeavesTheBarTransparent(int size, TrayState state)
    {
        ((byte, byte, byte, byte) quarter, (byte, byte, byte, byte) threeQuarters) = BarColumns(state, 0, size);

        Assert.Equal(Transparent, quarter);
        Assert.Equal(Transparent, threeQuarters);
    }

    [Theory]
    [MemberData(nameof(SizesAndBarStates))]
    public void Render_HalfFullDrive_FillsTheLeftHalfOfTheBarInTheStateColour(int size, TrayState state)
    {
        ((byte, byte, byte, byte) quarter, (byte, byte, byte, byte) threeQuarters) = BarColumns(state, 50, size);

        Assert.Equal(Opaque(TrayIconRenderer.BarColor(state)), quarter);
        Assert.Equal(Transparent, threeQuarters);
    }

    [Theory]
    [MemberData(nameof(SizesAndBarStates))]
    public void Render_FullDrive_FillsTheWholeBarInTheStateColour(int size, TrayState state)
    {
        ((byte, byte, byte, byte) quarter, (byte, byte, byte, byte) threeQuarters) = BarColumns(state, 100, size);

        Assert.Equal(Opaque(TrayIconRenderer.BarColor(state)), quarter);
        Assert.Equal(Opaque(TrayIconRenderer.BarColor(state)), threeQuarters);
    }

    [Fact]
    public void BarColor_OkAndAlert_AreBlueAndRed()
    {
        Color ok = TrayIconRenderer.BarColor(TrayState.Ok);
        Color alert = TrayIconRenderer.BarColor(TrayState.Alert);

        Assert.True(ok.B > ok.R && ok.B > ok.G, $"OK bar {ok} is not blue.");
        Assert.True(alert.R > alert.G && alert.R > alert.B, $"Alert bar {alert} is not red.");
    }

    [Theory]
    [MemberData(nameof(SizesAndStates))]
    public void Render_StatusDot_ShowsOnlyForAlertAndDisconnected(int size, TrayState state)
    {
        Int32Rect dot = TrayIconRenderer.DotBounds(size);
        (byte R, byte G, byte B, byte A) centre = RunOnSta(() =>
            Pixel(TrayIconRenderer.Render(new TrayIconKey(state, 50, TaskbarTheme.Dark), size), dot.X + (dot.Width / 2), dot.Y + (dot.Height / 2))
        );

        if (TrayIconRenderer.DotColor(state) is Color expected)
        {
            Assert.Equal(Opaque(expected), centre);
        }
        else
        {
            Assert.NotEqual(Opaque(TrayIconRenderer.DotColor(TrayState.Alert)!.Value), centre);
            Assert.NotEqual(Opaque(TrayIconRenderer.DotColor(TrayState.Disconnected)!.Value), centre);
        }
    }

    [Theory]
    [MemberData(nameof(SizesAndThemes))]
    public void Render_Outline_FollowsTheTaskbarTheme(int size, TaskbarTheme taskbar)
    {
        Int32Rect bar = TrayIconRenderer.BarBounds(size);
        (byte R, byte G, byte B, byte A) leftEdge = RunOnSta(() =>
            Pixel(TrayIconRenderer.Render(new TrayIconKey(TrayState.Ok, 50, taskbar), size), 0, bar.Y + (bar.Height / 2))
        );

        Assert.Equal(Opaque(TrayIconRenderer.OutlineColor(taskbar)), leftEdge);
    }

    [Fact]
    public void OutlineColor_LightTaskbar_IsDarkerThanOnADarkTaskbar()
    {
        Color onLight = TrayIconRenderer.OutlineColor(TaskbarTheme.Light);
        Color onDark = TrayIconRenderer.OutlineColor(TaskbarTheme.Dark);

        Assert.True(onLight.R + onLight.G + onLight.B < onDark.R + onDark.G + onDark.B);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void RenderIcon_HoldsEverySize_AndUsesThePreferredOne(int preferred)
    {
        (int Preferred, int[] Picked) result = RunOnSta(() =>
        {
            using DrawingIcon icon = TrayIconRenderer.RenderIcon(new TrayIconKey(TrayState.Alert, 95, TaskbarTheme.Light), preferred);
            int[] picked = [.. TrayIconRenderer.Sizes.Select(size => PickedWidth(icon, size))];
            return (icon.Width, picked);
        });

        Assert.Equal(preferred, result.Preferred);
        Assert.Equal(TrayIconRenderer.Sizes, result.Picked);
    }

    [Fact]
    public void Render_UnsupportedSize_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RunOnSta(() => TrayIconRenderer.Render(new TrayIconKey(TrayState.Ok, 0, TaskbarTheme.Dark), 48))
        );

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.02, 0)]
    [InlineData(0.03, 5)]
    [InlineData(0.52, 50)]
    [InlineData(0.53, 55)]
    [InlineData(0.97, 95)]
    [InlineData(0.98, 100)]
    [InlineData(1, 100)]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 100)]
    [InlineData(double.NaN, 0)]
    public void QuantiseFill_RoundsToTheNearestFivePercent(double fraction, int expected) =>
        Assert.Equal(expected, TrayIconKey.QuantiseFill(fraction));

    [Fact]
    public void Create_FractionsInTheSameStep_GiveEqualKeys() =>
        Assert.Equal(TrayIconKey.Create(TrayState.Ok, 0.49, TaskbarTheme.Dark), TrayIconKey.Create(TrayState.Ok, 0.52, TaskbarTheme.Dark));

    private static int PickedWidth(DrawingIcon icon, int size)
    {
        using DrawingIcon picked = new(icon, size, size);
        return picked.Width;
    }

    private static ((byte R, byte G, byte B, byte A) Quarter, (byte R, byte G, byte B, byte A) ThreeQuarters) BarColumns(
        TrayState state,
        int fillPercent,
        int size
    )
    {
        Int32Rect bar = TrayIconRenderer.BarBounds(size);
        return RunOnSta(() =>
        {
            BitmapSource bitmap = TrayIconRenderer.Render(new TrayIconKey(state, fillPercent, TaskbarTheme.Dark), size);
            return (Pixel(bitmap, bar.X + (bar.Width / 4), bar.Y), Pixel(bitmap, bar.X + (bar.Width * 3 / 4), bar.Y));
        });
    }

    private static (byte R, byte G, byte B, byte A) Opaque(Color color) => (color.R, color.G, color.B, 255);

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
}
