using System.IO.Pipes;
using System.Text;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service.Config;
using FreeSpaceWatcher.Service.Pipe;
using FreeSpaceWatcher.Service.Processes;
using FreeSpaceWatcher.Service.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace FreeSpaceWatcher.Service.Tests.Pipe;

/// <summary>A pipe server on a unique pipe name over a temp data folder, an in-memory status hub and real process actions.</summary>
internal sealed class PipeTestHarness : IAsyncDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly PipeServer _server;

    private PipeTestHarness()
    {
        ServicePaths paths = new(_temp.Path);
        Config = new ConfigService(paths, NullLogger<ConfigService>.Instance);
        History = new HistoryStore(paths.AlertsDirectory, null);
        PipeRequestHandler handler = new(
            Config,
            History,
            Hub,
            new ProcessActions(NullLogger<ProcessActions>.Instance),
            NullLogger<PipeRequestHandler>.Instance
        );
        _server = new PipeServer(handler, Hub, NullLogger<PipeServer>.Instance, PipeName);
    }

    public string PipeName { get; } = $"FreeSpaceWatcher.Tests.{Guid.NewGuid():N}";

    public StatusHub Hub { get; } = new();

    public ConfigService Config { get; }

    public HistoryStore History { get; }

    public static async Task<PipeTestHarness> StartAsync(CancellationToken cancellationToken)
    {
        PipeTestHarness harness = new();
        await harness._server.StartAsync(cancellationToken);
        return harness;
    }

    public async Task<PipeTestClient> ConnectAsync(CancellationToken cancellationToken)
    {
        NamedPipeClientStream stream = new(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await stream.ConnectAsync(5000, cancellationToken);
        return new PipeTestClient(stream);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _temp.Dispose();
    }
}

/// <summary>A pipe client that sends lines and reads one message per line, failing after 10 s of silence.</summary>
internal sealed class PipeTestClient(NamedPipeClientStream stream) : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);

    public Task SendAsync(PipeMessage message, CancellationToken cancellationToken) =>
        SendLineAsync(PipeProtocol.Serialize(message), cancellationToken);

    public async Task SendLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task<T> ReadAsync<T>(CancellationToken cancellationToken)
        where T : PipeMessage
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        string? line = await _reader.ReadLineAsync(timeout.Token);
        Assert.NotNull(line);
        return Assert.IsType<T>(PipeProtocol.Deserialize(line));
    }

    public async Task<T> RequestAsync<T>(PipeMessage request, CancellationToken cancellationToken)
        where T : PipeMessage
    {
        await SendAsync(request, cancellationToken);
        return await ReadAsync<T>(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await stream.DisposeAsync();
    }
}
