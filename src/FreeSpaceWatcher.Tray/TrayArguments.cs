namespace FreeSpaceWatcher.Tray;

/// <summary>The Fluent theme the tray's windows use.</summary>
public enum TrayTheme
{
    /// <summary>Follow the Windows light or dark setting.</summary>
    System,

    /// <summary>Always light.</summary>
    Light,

    /// <summary>Always dark.</summary>
    Dark,
}

/// <summary>A tray window that <c>--open</c> can name.</summary>
public enum TrayWindow
{
    /// <summary>The alerts window.</summary>
    Alerts,

    /// <summary>The settings window.</summary>
    Settings,

    /// <summary>The status window.</summary>
    Status,
}

/// <summary>
/// The tray's command line: <c>--theme light|dark|system</c>, <c>--open alerts|settings|status</c> and <c>--select-latest</c>, in
/// any order.
/// </summary>
/// <param name="Theme">The theme; <see cref="TrayTheme.System"/> unless <c>--theme</c> named another.</param>
/// <param name="Open">The window to open once the tray is up, or null.</param>
/// <param name="SelectLatest">Whether the alerts window that <c>--open alerts</c> opens selects the newest alert.</param>
/// <param name="Problems">One line per argument that was ignored, for the log.</param>
public sealed record TrayArguments(TrayTheme Theme, TrayWindow? Open, bool SelectLatest, IReadOnlyList<string> Problems)
{
    /// <summary>The switch that picks the theme.</summary>
    public const string ThemeSwitch = "--theme";

    /// <summary>The switch that names a window to open.</summary>
    public const string OpenSwitch = "--open";

    /// <summary>The switch that makes <c>--open alerts</c> select the newest alert.</summary>
    public const string SelectLatestSwitch = "--select-latest";

    /// <summary>
    /// Parses the command line; unknown arguments, unknown values, switches missing their value, and <c>--select-latest</c>
    /// without <c>--open alerts</c> are ignored and reported.
    /// </summary>
    /// <param name="args">The arguments, without the program name.</param>
    /// <returns>The parsed arguments; a later repeat of a switch wins.</returns>
    public static TrayArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        TrayTheme theme = TrayTheme.System;
        TrayWindow? open = null;
        bool selectLatest = false;
        List<string> problems = [];
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, SelectLatestSwitch, StringComparison.OrdinalIgnoreCase))
            {
                selectLatest = true;
                continue;
            }

            bool isTheme = string.Equals(arg, ThemeSwitch, StringComparison.OrdinalIgnoreCase);
            if (!isTheme && !string.Equals(arg, OpenSwitch, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Ignored unknown argument '{arg}'.");
                continue;
            }

            if (i + 1 >= args.Count)
            {
                problems.Add($"Ignored {arg}: it needs a value.");
                continue;
            }

            string value = args[++i];
            if (!(isTheme ? TryParseTheme(value, ref theme) : TryParseOpen(value, ref open)))
            {
                problems.Add($"Ignored {arg} '{value}': expected {(isTheme ? "light, dark or system" : "alerts, settings or status")}.");
            }
        }

        return new TrayArguments(theme, open, KeepSelectLatest(selectLatest, open, problems), problems);
    }

    /// <summary>Parses a window name as <c>--open</c> takes it, ignoring case.</summary>
    /// <param name="value">The name, e.g. "status".</param>
    /// <param name="window">The window, when the name is one.</param>
    /// <returns>Whether <paramref name="value"/> names a window.</returns>
    public static bool TryParseWindow(string? value, out TrayWindow window) => Enum.TryParse(value, ignoreCase: true, out window) && IsName(value);

    private static bool KeepSelectLatest(bool selectLatest, TrayWindow? open, List<string> problems)
    {
        if (!selectLatest || open == TrayWindow.Alerts)
        {
            return selectLatest;
        }

        problems.Add($"Ignored {SelectLatestSwitch}: it needs {OpenSwitch} alerts.");
        return false;
    }

    private static bool TryParseTheme(string value, ref TrayTheme theme)
    {
        if (!Enum.TryParse(value, ignoreCase: true, out TrayTheme parsed) || !IsName(value))
        {
            return false;
        }

        theme = parsed;
        return true;
    }

    private static bool TryParseOpen(string value, ref TrayWindow? open)
    {
        if (!TryParseWindow(value, out TrayWindow parsed))
        {
            return false;
        }

        open = parsed;
        return true;
    }

    // Enum.TryParse also accepts numbers ("1") and comma lists; only the names are valid on the command line.
    private static bool IsName(string? value) =>
        value is { Length: > 0 } && char.IsLetter(value[0]) && !value.Contains(',', StringComparison.Ordinal);
}
