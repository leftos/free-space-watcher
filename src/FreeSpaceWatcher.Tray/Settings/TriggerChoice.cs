using System.Globalization;
using System.Windows.Data;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>
/// The choices the settings offer for turning a trigger on or off, and how a choice's position in its drop-down maps to the setting:
/// a drive picks "Use default", "On" or "Off"; the defaults pick "On" or "Off".
/// </summary>
public static class TriggerChoice
{
    /// <summary>Gets a drive override's choices, in drop-down order.</summary>
    public static IReadOnlyList<string> OverrideLabels { get; } = ["Use default", "On", "Off"];

    /// <summary>Gets a default's choices, in drop-down order.</summary>
    public static IReadOnlyList<string> DefaultLabels { get; } = ["On", "Off"];

    /// <summary>Gets the position of a drive override in <see cref="OverrideLabels"/>.</summary>
    /// <param name="enabled">The override: null uses the default.</param>
    /// <returns>0 for null, 1 for on, 2 for off.</returns>
    public static int OverrideIndex(bool? enabled) =>
        enabled switch
        {
            null => 0,
            true => 1,
            false => 2,
        };

    /// <summary>Gets the drive override a position in <see cref="OverrideLabels"/> stands for.</summary>
    /// <param name="index">The position: 0, 1 or 2.</param>
    /// <returns>Null for 0, true for 1, false for 2.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is not 0, 1 or 2.</exception>
    public static bool? OverrideValue(int index) =>
        index switch
        {
            0 => null,
            1 => true,
            2 => false,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A trigger override is choice 0, 1 or 2."),
        };

    /// <summary>Gets the position of a default in <see cref="DefaultLabels"/>.</summary>
    /// <param name="enabled">Whether the trigger is on by default.</param>
    /// <returns>0 for on, 1 for off.</returns>
    public static int DefaultIndex(bool enabled) => enabled ? 0 : 1;

    /// <summary>Gets the default a position in <see cref="DefaultLabels"/> stands for.</summary>
    /// <param name="index">The position: 0 or 1.</param>
    /// <returns>True for 0, false for 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is not 0 or 1.</exception>
    public static bool DefaultValue(int index) =>
        index switch
        {
            0 => true,
            1 => false,
            _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A trigger default is choice 0 or 1."),
        };
}

/// <summary>Binds a drive's <c>bool?</c> trigger override to the selected index of a drop-down showing <see cref="TriggerChoice.OverrideLabels"/>.</summary>
public sealed class OverrideChoiceConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => TriggerChoice.OverrideIndex(value as bool?);

    /// <inheritdoc/>
    /// <remarks>A cleared selection (-1) leaves the setting alone.</remarks>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index and >= 0 and <= 2 ? TriggerChoice.OverrideValue(index) : Binding.DoNothing;
}

/// <summary>Binds a default's <c>bool</c> trigger setting to the selected index of a drop-down showing <see cref="TriggerChoice.DefaultLabels"/>.</summary>
public sealed class DefaultChoiceConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => TriggerChoice.DefaultIndex(value is true);

    /// <inheritdoc/>
    /// <remarks>A cleared selection (-1) leaves the setting alone.</remarks>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int index and (0 or 1) ? TriggerChoice.DefaultValue(index) : Binding.DoNothing;
}
