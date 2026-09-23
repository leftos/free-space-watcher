using System.Text;
using System.Threading.Channels;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Service.Pipe;

/// <summary>One client's write side: responses and pushes go out as UTF-8 lines, one at a time, never interleaved.</summary>
/// <param name="stream">The connected pipe.</param>
internal sealed class PipeConnection(Stream stream) : IDisposable
{
    private const int PushBacklog = 64;
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<PipeMessage> _pushes = Channel.CreateBounded<PipeMessage>(
        new BoundedChannelOptions(PushBacklog) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }
    );

    /// <summary>Writes one message as one line.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the wait for the write lock and the write.</param>
    /// <returns>A task that completes when the line is flushed.</returns>
    public async Task SendAsync(PipeMessage message, CancellationToken cancellationToken)
    {
        byte[] line = Utf8.GetBytes(PipeProtocol.Serialize(message) + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Queues a push without blocking; when the client falls 64 pushes behind, the oldest is dropped.</summary>
    /// <param name="message">The push.</param>
    public void EnqueuePush(PipeMessage message) => _pushes.Writer.TryWrite(message);

    /// <summary>Sends queued pushes until <see cref="CompletePushes"/> is called or the token is cancelled.</summary>
    /// <param name="cancellationToken">Stops the sending.</param>
    /// <returns>A task that completes when the queue is closed and drained.</returns>
    public async Task PumpPushesAsync(CancellationToken cancellationToken)
    {
        await foreach (PipeMessage push in _pushes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendAsync(push, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the push queue.</summary>
    public void CompletePushes() => _pushes.Writer.TryComplete();

    /// <inheritdoc/>
    public void Dispose() => _writeLock.Dispose();
}
