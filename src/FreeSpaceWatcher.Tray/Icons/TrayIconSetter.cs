using H.NotifyIcon;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Icons;

/// <summary>Shows the icon of a tray state on the taskbar icon, and keeps the rendered icons the tray owns.</summary>
/// <remarks>
/// The taskbar icon owns the icon it is given: it disposes the icon it held each time the value changes, and disposes the
/// last one when it is disposed itself. Every assignment here therefore hands over a fresh clone, and the rendered icons
/// stay the caller's to dispose.
/// </remarks>
public sealed class TrayIconSetter
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly IReadOnlyDictionary<TrayState, DrawingIcon> _icons;
    private TrayState? _applied;

    /// <summary>Initializes the setter.</summary>
    /// <param name="taskbarIcon">The taskbar icon the state is shown on.</param>
    /// <param name="icons">The rendered icon of each state; the caller keeps ownership of them.</param>
    public TrayIconSetter(TaskbarIcon taskbarIcon, IReadOnlyDictionary<TrayState, DrawingIcon> icons)
    {
        ArgumentNullException.ThrowIfNull(taskbarIcon);
        ArgumentNullException.ThrowIfNull(icons);
        _taskbarIcon = taskbarIcon;
        _icons = icons;
    }

    /// <summary>Gets how many icons have been handed to the taskbar icon.</summary>
    public int Assignments { get; private set; }

    /// <summary>Shows the icon of a state; the taskbar icon is left alone when the state has not changed.</summary>
    /// <param name="state">The state to show.</param>
    public void Apply(TrayState state)
    {
        if (_applied == state)
        {
            return;
        }

        // The taskbar icon disposes the icon it held whenever this changes, so it gets its own clone of the rendered icon.
        _taskbarIcon.Icon = (DrawingIcon)_icons[state].Clone();
        _applied = state;
        Assignments++;
    }
}
