using System.Windows;
using System.Windows.Media;
using FreeSpaceWatcher.Core.Formatting;
using FreeSpaceWatcher.Core.Triggers;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Controls;

/// <summary>
/// A line of a drive's free space over the last <see cref="SampleHistory.Span"/>, with a dashed line at the floor when in range and
/// a tooltip giving the least and most free space in view.
/// </summary>
public sealed partial class Sparkline
{
    /// <summary>Identifies the <see cref="Samples"/> property.</summary>
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<DriveSample>),
        typeof(Sparkline),
        new PropertyMetadata(null, (d, _) => ((Sparkline)d).Redraw())
    );

    /// <summary>Identifies the <see cref="FloorBytes"/> property.</summary>
    public static readonly DependencyProperty FloorBytesProperty = DependencyProperty.Register(
        nameof(FloorBytes),
        typeof(long?),
        typeof(Sparkline),
        new PropertyMetadata(null, (d, _) => ((Sparkline)d).Redraw())
    );

    /// <summary>Initializes a new instance of the <see cref="Sparkline"/> class.</summary>
    public Sparkline()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>Gets or sets the samples, oldest first; the newest one's time is the right edge.</summary>
    public IReadOnlyList<DriveSample>? Samples
    {
        get => (IReadOnlyList<DriveSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    /// <summary>Gets or sets the drive's floor in bytes, or null for no floor line.</summary>
    public long? FloorBytes
    {
        get => (long?)GetValue(FloorBytesProperty);
        set => SetValue(FloorBytesProperty, value);
    }

    private void Redraw()
    {
        IReadOnlyList<DriveSample> samples = Samples ?? [];
        DateTimeOffset end = samples.Count > 0 ? samples[^1].Time : DateTimeOffset.MinValue;
        Size area = new(ActualWidth, ActualHeight);
        var geometry = SparklineGeometry.Map(samples, end, SampleHistory.Span, area, FloorBytes);
        Trace.Points = [.. geometry.Points];
        ToolTip = geometry is { MinFreeBytes: long min, MaxFreeBytes: long max }
            ? $"Free space in view: {ByteFormat.Format(min)} to {ByteFormat.Format(max)}"
            : null;
        if (geometry.FloorY is double y)
        {
            FloorLine.X1 = 0;
            FloorLine.X2 = area.Width;
            FloorLine.Y1 = y;
            FloorLine.Y2 = y;
            FloorLine.Visibility = Visibility.Visible;
        }
        else
        {
            FloorLine.Visibility = Visibility.Collapsed;
        }
    }
}
