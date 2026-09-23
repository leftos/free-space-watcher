using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Tray.Pipe;

namespace FreeSpaceWatcher.Tray.Tests.Pipe;

/// <summary>A one-client pipe server under a unique name, driven step by step by a test.</summary>
internal sealed class FakePipeServer
{
    public string Name { get; } = "FreeSpaceWatcher.Tests." + Guid.NewGuid().ToString("N");

    public async Task<FakePipeSession> AcceptAsync(CancellationToken cancellationToken)
    {
        NamedPipeServerStream stream = new(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        try
        {
            await stream.WaitForConnectionAsync(cancellationToken);
            return new FakePipeSession(stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}

/// <summary>The server side of one connection: reads requests and writes responses and pushes as JSON lines.</summary>
/// <param name="stream">The connected server stream.</param>
internal sealed class FakePipeSession(NamedPipeServerStream stream) : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly StreamReader _reader = new(stream, Utf8, false, 4096, leaveOpen: true);

    public static StatusResponse Status(string letter) =>
        new(
            [
                new DriveStatus
                {
                    Letter = letter,
                    Watched = true,
                    Available = true,
                    FreeBytes = 1,
                    TotalBytes = 2,
                    DropRateBytesPerSecond = null,
                    TimeToFull = null,
                },
            ],
            true,
            null
        );

    public async Task<PipeMessage> ReceiveAsync(CancellationToken cancellationToken)
    {
        string line = await _reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("The client closed the pipe.");
        return PipeProtocol.Deserialize(line);
    }

    public async Task SendAsync(PipeMessage message, CancellationToken cancellationToken)
    {
        byte[] line = Utf8.GetBytes(PipeProtocol.Serialize(message) + "\n");
        await stream.WriteAsync(line, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public async Task AnswerSubscribeAsync(CancellationToken cancellationToken)
    {
        PipeMessage request = await ReceiveAsync(cancellationToken);
        Assert.IsType<SubscribeRequest>(request);
        await SendAsync(new StatusResponse([], false, null) { RequestId = request.RequestId }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await stream.DisposeAsync();
    }
}

/// <summary>Queues what a <see cref="PipeClient"/> raises, so a test can await each event in order.</summary>
internal sealed class ClientEvents
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly Channel<bool> _connections = Channel.CreateUnbounded<bool>();
    private readonly Channel<StatusResponse> _statuses = Channel.CreateUnbounded<StatusResponse>();
    private readonly Channel<Alert> _alerts = Channel.CreateUnbounded<Alert>();

    public ClientEvents(PipeClient client)
    {
        client.ConnectionChanged += (_, connected) => _connections.Writer.TryWrite(connected);
        client.StatusReceived += (_, status) => _statuses.Writer.TryWrite(status);
        client.AlertReceived += (_, alert) => _alerts.Writer.TryWrite(alert);
    }

    public Task<bool> NextConnectionAsync(CancellationToken cancellationToken) => NextAsync(_connections, cancellationToken);

    public Task<StatusResponse> NextStatusAsync(CancellationToken cancellationToken) => NextAsync(_statuses, cancellationToken);

    public Task<Alert> NextAlertAsync(CancellationToken cancellationToken) => NextAsync(_alerts, cancellationToken);

    private static async Task<T> NextAsync<T>(Channel<T> channel, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Wait);
        return await channel.Reader.ReadAsync(timeout.Token);
    }
}
