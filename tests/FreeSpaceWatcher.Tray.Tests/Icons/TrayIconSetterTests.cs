using System.Runtime.ExceptionServices;
using FreeSpaceWatcher.Tray.Icons;
using H.NotifyIcon;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Tests.Icons;

/// <summary>The setter hands the taskbar icon a clone, so the rendered icons it keeps stay usable, and skips unchanged keys.</summary>
public sealed class TrayIconSetterTests
{
    private static readonly TrayState[] CrashSequence =
    [
        TrayState.Ok,
        TrayState.Alert,
        TrayState.Ok,
        TrayState.Alert,
        TrayState.Disconnected,
        TrayState.Ok,
    ];

    [Fact]
    public void Apply_StatesAlternatingBetweenOkAlertAndDisconnected_AssignsEachStateAndDoesNotThrow()
    {
        (int Assignments, int Renders) counts = Run(setter =>
        {
            foreach (TrayState state in CrashSequence)
            {
                setter.Apply(TrayIconKey.Create(state, 0.5, TaskbarTheme.Dark));
            }
        });

        Assert.Equal((CrashSequence.Length, 3), counts);
    }

    [Fact]
    public void Apply_FillMovesWithinOneFivePercentStep_AssignsOnce()
    {
        (int Assignments, int Renders) counts = Run(setter =>
        {
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.51, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.52, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.49, TaskbarTheme.Dark));
        });

        Assert.Equal((1, 1), counts);
    }

    [Fact]
    public void Apply_FillCrossesAStep_Reassigns()
    {
        (int Assignments, int Renders) counts = Run(setter =>
        {
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.52, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.53, TaskbarTheme.Dark));
        });

        Assert.Equal((2, 2), counts);
    }

    [Fact]
    public void Apply_TaskbarThemeChanges_Reassigns()
    {
        (int Assignments, int Renders) counts = Run(setter =>
        {
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.5, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.5, TaskbarTheme.Light));
        });

        Assert.Equal((2, 2), counts);
    }

    [Fact]
    public void Apply_KeySeenBefore_ReusesItsRenderedIcon()
    {
        (int Assignments, int Renders) counts = Run(setter =>
        {
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.5, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Alert, 0.5, TaskbarTheme.Dark));
            setter.Apply(TrayIconKey.Create(TrayState.Ok, 0.5, TaskbarTheme.Dark));
        });

        Assert.Equal((3, 2), counts);
    }

    private static (int Assignments, int Renders) Run(Action<TrayIconSetter> work) =>
        RunOnSta(() =>
        {
            using TaskbarIcon taskbarIcon = new();
            using TrayIconSetter setter = new(taskbarIcon, key => TrayIconRenderer.RenderIcon(key, 16));
            work(setter);
            return (setter.Assignments, setter.Renders);
        });

    private static T RunOnSta<T>(Func<T> work)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }
}
