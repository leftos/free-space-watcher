using H.NotifyIcon;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>
/// Shows a tray icon key on the taskbar icon: renders each key once, keeps the rendered icons, and changes the taskbar icon only
/// when the state, the quantised fill or the taskbar theme changed.
/// </summary>
/// <remarks>
/// The taskbar icon owns the icon it is given: it disposes the icon it held each time the value changes, and disposes the
/// last one when it is disposed itself. Every assignment here therefore hands over a fresh clone, and the rendered icons
/// stay the setter's until it is disposed.
/// </remarks>
public sealed class TrayIconSetter : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly Func<TrayIconKey, DrawingIcon> _render;
    private readonly Dictionary<TrayIconKey, DrawingIcon> _rendered = [];
    private TrayIconKey? _applied;

    /// <summary>Initializes the setter.</summary>
    /// <param name="taskbarIcon">The taskbar icon the keys are shown on.</param>
    /// <param name="render">Draws the icon of a key; the setter owns and disposes what it returns.</param>
    public TrayIconSetter(TaskbarIcon taskbarIcon, Func<TrayIconKey, DrawingIcon> render)
    {
        ArgumentNullException.ThrowIfNull(taskbarIcon);
        ArgumentNullException.ThrowIfNull(render);
        _taskbarIcon = taskbarIcon;
        _render = render;
    }

    /// <summary>Gets how many icons have been handed to the taskbar icon.</summary>
    public int Assignments { get; private set; }

    /// <summary>Gets how many distinct keys have been rendered.</summary>
    public int Renders => _rendered.Count;

    /// <summary>Shows the icon of a key; the taskbar icon is left alone when the key has not changed.</summary>
    /// <param name="key">The key to show.</param>
    public void Apply(TrayIconKey key)
    {
        if (_applied == key)
        {
            return;
        }

        if (!_rendered.TryGetValue(key, out DrawingIcon? icon))
        {
            icon = _render(key);
            _rendered[key] = icon;
        }

        // The taskbar icon disposes the icon it held whenever this changes, so it gets its own clone of the rendered icon.
        _taskbarIcon.Icon = (DrawingIcon)icon.Clone();
        _applied = key;
        Assignments++;
    }

    /// <summary>Disposes the rendered icons; dispose the taskbar icon first.</summary>
    public void Dispose()
    {
        foreach (DrawingIcon icon in _rendered.Values)
        {
            icon.Dispose();
        }

        _rendered.Clear();
    }
}
