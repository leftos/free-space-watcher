using CommunityToolkit.Mvvm.ComponentModel;

namespace FreeSpaceWatcher.Tray.Settings;

/// <summary>One numeric settings input: the typed text, its validation error, and a placeholder shown while it is blank.</summary>
/// <param name="unit">The unit the field is typed in.</param>
/// <param name="allowBlank">Whether blank is accepted, meaning "use the default".</param>
/// <param name="culture">The culture the text is parsed and formatted with.</param>
public sealed partial class NumberField(InputUnit unit, bool allowBlank, IFormatProvider culture) : ObservableObject
{
    private readonly bool _allowBlank = allowBlank;
    private readonly IFormatProvider _culture = culture;

    /// <summary>Gets the unit the field is typed in.</summary>
    public InputUnit Unit { get; } = unit;

    /// <summary>Gets or sets the typed text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBlank))]
    public partial string Text { get; set; } = "";

    /// <summary>Gets or sets the text shown while the field is blank, e.g. the default it falls back to.</summary>
    [ObservableProperty]
    public partial string Placeholder { get; set; } = "";

    /// <summary>Gets why the text is not accepted, or null when it is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; } = allowBlank ? null : "Required";

    /// <summary>Gets whether the field is blank.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Text);

    /// <summary>Gets whether the text is not accepted.</summary>
    public bool HasError => Error is not null;

    /// <summary>Gets the value in configuration units, or null when blank or not accepted.</summary>
    public double? Value => UnitInput.Parse(Text, Unit, _allowBlank, _culture).Value;

    /// <summary>Gets the value rounded to a whole number of configuration units, or null when blank or not accepted.</summary>
    public long? RoundedValue => Value is double value ? (long)Math.Round(value) : null;

    /// <summary>Shows a configuration value in the field's unit, or blanks the field.</summary>
    /// <param name="value">The value in configuration units, or null for blank.</param>
    public void SetValue(double? value) => Text = value is double v ? UnitInput.Format(v, Unit, _culture) : "";

    partial void OnTextChanged(string value) => Error = UnitInput.Parse(value, Unit, _allowBlank, _culture).Error;
}
