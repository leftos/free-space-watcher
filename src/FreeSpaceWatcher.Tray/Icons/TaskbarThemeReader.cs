using Microsoft.Win32;

namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>Reads whether the taskbar is light or dark from the current user's personalization settings.</summary>
public static class TaskbarThemeReader
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string SystemUsesLightThemeValue = "SystemUsesLightTheme";

    /// <summary>Reads <c>HKCU\...\Themes\Personalize\SystemUsesLightTheme</c>.</summary>
    /// <returns><see cref="TaskbarTheme.Light"/> when the value is 1; <see cref="TaskbarTheme.Dark"/> when it is 0 or missing.</returns>
    public static TaskbarTheme Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(SystemUsesLightThemeValue) is int value && value == 1 ? TaskbarTheme.Light : TaskbarTheme.Dark;
    }
}
