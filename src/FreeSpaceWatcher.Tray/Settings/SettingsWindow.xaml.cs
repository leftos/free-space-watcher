using System.Windows;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>The settings window; its behaviour lives in <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
