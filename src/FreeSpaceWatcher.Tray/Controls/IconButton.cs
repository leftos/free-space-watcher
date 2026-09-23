using System.Windows;

namespace FreeSpaceWatcher.Tray.Controls;

/// <summary>The glyph a button styled <c>IconButton</c> shows, as an attached property.</summary>
public static class IconButton
{
    /// <summary>Identifies the Glyph attached property: one Segoe Fluent Icons character, e.g. "&#xE713;" for Settings.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph",
        typeof(string),
        typeof(IconButton),
        new FrameworkPropertyMetadata(string.Empty)
    );

    /// <summary>Gets an element's glyph.</summary>
    /// <param name="element">The element.</param>
    /// <returns>The glyph.</returns>
    public static string GetGlyph(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(GlyphProperty);
    }

    /// <summary>Sets an element's glyph.</summary>
    /// <param name="element">The element.</param>
    /// <param name="value">The glyph.</param>
    public static void SetGlyph(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(GlyphProperty, value);
    }
}
