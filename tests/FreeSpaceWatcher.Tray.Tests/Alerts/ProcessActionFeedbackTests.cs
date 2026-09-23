using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Core.Writes;
using FreeSpaceWatcher.Tray.Alerts;

namespace FreeSpaceWatcher.Tray.Tests.Alerts;

public sealed class ProcessActionFeedbackTests
{
    private const int Pid = 41372;

    [Theory]
    [InlineData(ProcessAction.Suspend, "Suspended pwsh (pid 41372).")]
    [InlineData(ProcessAction.Resume, "Resumed pwsh (pid 41372).")]
    [InlineData(ProcessAction.Kill, "Killed pwsh (pid 41372).")]
    public void StatusLine_Success_NamesTheActionDone(ProcessAction action, string expected)
    {
        ProcessActionResponse response = new(true, false, null, ProcessState.Running);

        string status = ProcessActionText.StatusLine(action, "pwsh", Pid, ProcessActionText.ErrorOf(response));

        Assert.Equal(expected, status);
    }

    [Fact]
    public void StatusLine_ServiceError_ShowsTheError()
    {
        ProcessActionResponse response = new(false, false, "No process with id 41372 is running.", ProcessState.Exited);

        string status = ProcessActionText.StatusLine(ProcessAction.Suspend, "pwsh", Pid, ProcessActionText.ErrorOf(response));

        Assert.Equal("Suspend pwsh (pid 41372) failed: No process with id 41372 is running.", status);
    }

    [Fact]
    public void StatusLine_ServiceErrorWithoutReason_SaysSo()
    {
        ProcessActionResponse response = new(false, false, null, null);

        string status = ProcessActionText.StatusLine(ProcessAction.Resume, "pwsh", Pid, ProcessActionText.ErrorOf(response));

        Assert.Equal("Resume pwsh (pid 41372) failed: The service gave no reason.", status);
    }

    [Fact]
    public void StatusLine_PipeFailure_ShowsTheMessage()
    {
        string status = ProcessActionText.StatusLine(ProcessAction.Kill, "pwsh", Pid, "The service is not running.");

        Assert.Equal("Kill pwsh (pid 41372) failed: The service is not running.", status);
    }

    [Fact]
    public void Denied_NamesTheProcess() => Assert.Equal("Access to pwsh (pid 41372) was denied.", ProcessActionText.Denied("pwsh", Pid));

    [Fact]
    public void Toast_SucceededAndFailedHeadings()
    {
        Assert.Equal("Suspended pwsh (pid 41372)", ProcessActionText.Succeeded(ProcessAction.Suspend, "pwsh", Pid));
        Assert.Equal("Suspend pwsh (pid 41372) failed", ProcessActionText.Failed(ProcessAction.Suspend, "pwsh", Pid));
    }

    [Theory]
    [InlineData(null, true, false, "")]
    [InlineData(ProcessState.Running, true, false, "")]
    [InlineData(ProcessState.Suspended, false, true, " · Suspended")]
    [InlineData(ProcessState.Exited, false, false, " · Exited")]
    public void ProcessNode_ButtonsAndSuffix_FollowTheState(ProcessState? state, bool canSuspend, bool canResume, string suffix)
    {
        var node = ProcessNode.From(Report());

        node.State = state;

        Assert.Equal(canSuspend, node.CanSuspend);
        Assert.Equal(canResume, node.CanResume);
        Assert.EndsWith("0 deleted" + suffix, node.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ProcessNode_StateChange_NotifiesTitleAndButtons()
    {
        var node = ProcessNode.From(Report());
        List<string?> changed = [];
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        node.State = ProcessState.Suspended;

        Assert.Contains(nameof(ProcessNode.Title), changed);
        Assert.Contains(nameof(ProcessNode.CanSuspend), changed);
        Assert.Contains(nameof(ProcessNode.CanResume), changed);
    }

    private static ProcessWriteReport Report() =>
        new()
        {
            ProcessId = Pid,
            Name = "pwsh",
            ExePath = null,
            StartTime = null,
            BytesWritten = 1L << 30,
            ExtendBytes = 0,
            FilesCreated = 2,
            FilesDeleted = 0,
            Folders = [],
            Files = [],
        };
}
