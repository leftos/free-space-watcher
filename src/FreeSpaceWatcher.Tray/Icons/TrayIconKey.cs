namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>Whether the taskbar is light or dark, which decides the colour of the icon's outline.</summary>
public enum TaskbarTheme
{
    /// <summary>A dark taskbar: the outline is light.</summary>
    Dark,

    /// <summary>A light taskbar: the outline is dark.</summary>
    Light,
}

/// <summary>Everything a tray icon shows: the state, the used fraction of the fullest watched drive in 5 % steps, and the taskbar theme.</summary>
/// <param name="State">The state, which picks the bar colour and the status dot.</param>
/// <param name="FillPercent">How full the usage bar is, a multiple of <see cref="FillStepPercent"/> from 0 to 100.</param>
/// <param name="Taskbar">The taskbar theme, which picks the outline colour.</param>
public readonly record struct TrayIconKey(TrayState State, int FillPercent, TaskbarTheme Taskbar)
{
    /// <summary>The step the fill is quantised to, so the icon changes at most once per 5 % of the drive.</summary>
    public const int FillStepPercent = 5;

    /// <summary>Builds the key for a state, a used fraction and a taskbar theme.</summary>
    /// <param name="state">The state.</param>
    /// <param name="usedFraction">The used fraction of the fullest watched drive, from 0 to 1.</param>
    /// <param name="taskbar">The taskbar theme.</param>
    /// <returns>The key, with the fraction quantised by <see cref="QuantiseFill"/>.</returns>
    public static TrayIconKey Create(TrayState state, double usedFraction, TaskbarTheme taskbar) => new(state, QuantiseFill(usedFraction), taskbar);

    /// <summary>Rounds a used fraction to the nearest <see cref="FillStepPercent"/> step, halves rounding up.</summary>
    /// <param name="usedFraction">The fraction; values outside 0 to 1 are clamped and NaN reads as 0.</param>
    /// <returns>A percentage from 0 to 100 that is a multiple of <see cref="FillStepPercent"/>.</returns>
    public static int QuantiseFill(double usedFraction)
    {
        if (double.IsNaN(usedFraction))
        {
            return 0;
        }

        double steps = Math.Clamp(usedFraction, 0, 1) * 100 / FillStepPercent;
        return (int)Math.Round(steps, MidpointRounding.AwayFromZero) * FillStepPercent;
    }
}
