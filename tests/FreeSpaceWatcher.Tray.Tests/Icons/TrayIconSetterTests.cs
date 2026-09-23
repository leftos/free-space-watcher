using System.Runtime.ExceptionServices;
using FreeSpaceWatcher.Tray.Icons;
using H.NotifyIcon;
using DrawingIcon = System.Drawing.Icon;

namespace FreeSpaceWatcher.Tray.Tests.Icons;

/// <summary>The setter hands the taskbar icon a clone, so the rendered icons the tray keeps stay usable.</summary>
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
        RunOnSta(() =>
        {
            using TaskbarIcon taskbarIcon = new();
            Dictionary<TrayState, DrawingIcon> icons = RenderAll();
            try
            {
                TrayIconSetter setter = new(taskbarIcon, icons);

                foreach (TrayState state in CrashSequence)
                {
                    setter.Apply(state);
                }

                Assert.Equal(CrashSequence.Length, setter.Assignments);
            }
            finally
            {
                DisposeAll(icons);
            }
        });
    }

    [Fact]
    public void Apply_SameStateThreeTimes_AssignsOnce()
    {
        RunOnSta(() =>
        {
            using TaskbarIcon taskbarIcon = new();
            Dictionary<TrayState, DrawingIcon> icons = RenderAll();
            try
            {
                TrayIconSetter setter = new(taskbarIcon, icons);

                setter.Apply(TrayState.Ok);
                setter.Apply(TrayState.Ok);
                setter.Apply(TrayState.Ok);

                Assert.Equal(1, setter.Assignments);
            }
            finally
            {
                DisposeAll(icons);
            }
        });
    }

    private static Dictionary<TrayState, DrawingIcon> RenderAll() =>
        Enum.GetValues<TrayState>().ToDictionary(state => state, state => TrayIconRenderer.ToIcon(TrayIconRenderer.Render(state)));

    private static void DisposeAll(Dictionary<TrayState, DrawingIcon> icons)
    {
        foreach (DrawingIcon icon in icons.Values)
        {
            icon.Dispose();
        }
    }

    private static void RunOnSta(Action work) =>
        RunOnSta<object?>(() =>
        {
            work();
            return null;
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
