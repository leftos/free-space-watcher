using System.Windows;
using FreeSpaceWatcher.Tray.Status;

namespace FreeSpaceWatcher.Tray.Controls;

/// <summary>A capsule showing how much of a drive is used, coloured by the drive's <see cref="DriveSeverity"/>.</summary>
public sealed partial class FreeSpaceBar
{
    /// <summary>Identifies the <see cref="UsedFraction"/> property.</summary>
    public static readonly DependencyProperty UsedFractionProperty = DependencyProperty.Register(
        nameof(UsedFraction),
        typeof(double),
        typeof(FreeSpaceBar),
        new PropertyMetadata(0.0, (d, _) => ((FreeSpaceBar)d).Refresh())
    );

    /// <summary>Identifies the <see cref="Severity"/> property.</summary>
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity),
        typeof(DriveSeverity),
        typeof(FreeSpaceBar),
        new PropertyMetadata(DriveSeverity.Healthy, (d, _) => ((FreeSpaceBar)d).Refresh())
    );

    /// <summary>Initializes a new instance of the <see cref="FreeSpaceBar"/> class.</summary>
    public FreeSpaceBar()
    {
        InitializeComponent();
        Refresh();
    }

    /// <summary>Gets or sets the used fraction of the drive, from 0 to 1; values outside are clamped.</summary>
    public double UsedFraction
    {
        get => (double)GetValue(UsedFractionProperty);
        set => SetValue(UsedFractionProperty, value);
    }

    /// <summary>Gets or sets the drive's severity: accent colour when healthy, else the caution or critical colour.</summary>
    public DriveSeverity Severity
    {
        get => (DriveSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    private void Refresh()
    {
        double used = double.IsNaN(UsedFraction) ? 0 : Math.Clamp(UsedFraction, 0, 1);
        UsedColumn.Width = new GridLength(used, GridUnitType.Star);
        FreeColumn.Width = new GridLength(1 - used, GridUnitType.Star);
        Fill.SetResourceReference(
            BackgroundProperty,
            Severity switch
            {
                DriveSeverity.Critical => "SystemFillColorCriticalBrush",
                DriveSeverity.Caution => "SystemFillColorCautionBrush",
                _ => "AccentFillColorDefaultBrush",
            }
        );
    }
}
