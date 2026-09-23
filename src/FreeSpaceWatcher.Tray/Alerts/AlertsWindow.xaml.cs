using System.Windows;

namespace FreeSpaceWatcher.Tray.Alerts;

/// <summary>The alerts window; its behaviour lives in <see cref="AlertsViewModel"/>.</summary>
public partial class AlertsWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public AlertsWindow()
    {
        InitializeComponent();
    }
}
