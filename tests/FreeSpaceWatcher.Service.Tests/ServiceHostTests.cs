using System.IO.Pipes;

namespace FreeSpaceWatcher.Service.Tests;

public sealed class ServiceHostTests
{
    [Fact]
    public async Task RunAsync_PipeAlreadyServed_ExitsWith2_WithoutTouchingTheDataFolder()
    {
        string pipeName = $"FreeSpaceWatcher.Tests.{Guid.NewGuid():N}";
        using NamedPipeServerStream held = new(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );
        using TempDirectory temp = new();

        int exitCode = await ServiceHost.RunAsync(["--data-dir", temp.Path], pipeName);

        Assert.Equal(2, exitCode);
        Assert.False(File.Exists(Path.Combine(temp.Path, "config.json")), "the second instance wrote config.json");
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }
}
