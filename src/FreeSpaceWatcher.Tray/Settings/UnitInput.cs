using System.Globalization;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>A unit a settings field is typed in, and how it converts to the configuration's unit.</summary>
/// <param name="Label">The unit's label, e.g. "GB/min".</param>
/// <param name="Factor">How many configuration units one typed unit is, e.g. 1073741824 bytes per GB.</param>
/// <param name="WholeNumber">Whether only whole numbers are accepted.</param>
/// <param name="Maximum">The largest value accepted, in typed units, if there is one.</param>
public sealed record InputUnit(string Label, double Factor, bool WholeNumber, double? Maximum)
{
    /// <summary>Gets gigabytes (binary, 1 GB = 2^30 bytes), converted to bytes.</summary>
    public static InputUnit Gigabytes { get; } = new("GB", 1L << 30, false, null);

    /// <summary>Gets gigabytes per minute, converted to bytes per minute.</summary>
    public static InputUnit GigabytesPerMinute { get; } = new("GB/min", 1L << 30, false, null);

    /// <summary>Gets megabytes per minute, converted to bytes per minute.</summary>
    public static InputUnit MegabytesPerMinute { get; } = new("MB/min", 1L << 20, false, null);

    /// <summary>Gets minutes, fractions allowed.</summary>
    public static InputUnit Minutes { get; } = new("min", 1, false, null);

    /// <summary>Gets whole minutes.</summary>
    public static InputUnit WholeMinutes { get; } = new("min", 1, true, int.MaxValue);

    /// <summary>Gets a percentage, up to 100.</summary>
    public static InputUnit Percent { get; } = new("%", 1, false, 100);

    /// <summary>Gets whole seconds.</summary>
    public static InputUnit Seconds { get; } = new("s", 1, true, int.MaxValue);

    /// <summary>Gets whole days.</summary>
    public static InputUnit Days { get; } = new("days", 1, true, int.MaxValue);

    /// <summary>Gets a whole count.</summary>
    public static InputUnit Count { get; } = new("", 1, true, int.MaxValue);

    /// <summary>Gets the grace delay's whole seconds, from 0 (off) to 600.</summary>
    public static InputUnit GraceSeconds { get; } = new("s", 1, true, 600) { AllowsZero = true };

    /// <summary>Gets the auto-resolve window's whole minutes, from 0 (off) to 1440.</summary>
    public static InputUnit ResolveMinutes { get; } = new("min", 1, true, 1440) { AllowsZero = true };

    /// <summary>Gets whether 0 is accepted, for a setting that 0 turns off; otherwise values must be greater than 0.</summary>
    public bool AllowsZero { get; init; }
}

/// <summary>The outcome of parsing a settings field.</summary>
/// <param name="Value">The value in configuration units; null for a blank field or an error.</param>
/// <param name="Error">Why the text is not accepted, or null when it is.</param>
public readonly record struct ParsedInput(double? Value, string? Error);

/// <summary>Converts settings text in human units to configuration values and back.</summary>
public static class UnitInput
{
    /// <summary>Parses a field's text; never throws on bad input.</summary>
    /// <param name="text">The typed text.</param>
    /// <param name="unit">The unit the text is in.</param>
    /// <param name="allowBlank">Whether a blank field is accepted (as no value).</param>
    /// <param name="culture">The culture tried first; the invariant culture is tried next.</param>
    /// <returns>The value in configuration units, nothing for an accepted blank, or an error.</returns>
    public static ParsedInput Parse(string? text, InputUnit unit, bool allowBlank, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (string.IsNullOrWhiteSpace(text))
        {
            return allowBlank ? new ParsedInput(null, null) : new ParsedInput(null, "Required");
        }

        string trimmed = text.Trim();
        bool parsed =
            double.TryParse(trimmed, NumberStyles.Float, culture, out double typed)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out typed);
        return parsed && double.IsFinite(typed) ? Check(typed, unit) : new ParsedInput(null, $"'{trimmed}' is not a number");
    }

    /// <summary>Formats a configuration value in a unit, with up to three decimals.</summary>
    /// <param name="value">The value in configuration units.</param>
    /// <param name="unit">The unit to show it in.</param>
    /// <param name="culture">The culture to format with.</param>
    /// <returns>The text, e.g. "1.5" for 1610612736 bytes in GB.</returns>
    public static string Format(double value, InputUnit unit, IFormatProvider culture)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return (value / unit.Factor).ToString("0.###", culture);
    }

    private static ParsedInput Check(double typed, InputUnit unit)
    {
        if (typed < 0 || (typed == 0 && !unit.AllowsZero))
        {
            return new ParsedInput(null, unit.AllowsZero ? "Must be 0 or more" : "Must be greater than 0");
        }

        if (unit.Maximum is double maximum && typed > maximum)
        {
            return new ParsedInput(null, string.Create(CultureInfo.CurrentCulture, $"Must be at most {maximum}"));
        }

        if (unit.WholeNumber && typed != Math.Floor(typed))
        {
            return new ParsedInput(null, "Must be a whole number");
        }

        double value = typed * unit.Factor;
        return value >= long.MaxValue ? new ParsedInput(null, "Too large") : new ParsedInput(value, null);
    }
}
