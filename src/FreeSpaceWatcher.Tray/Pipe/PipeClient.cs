using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Tray.Pipe;

/// <summary>The tray's connection to the service pipe: subscribes on connect, matches responses to requests, reconnects forever.</summary>
/// <remarks>
/// Events are raised on a pool thread; consumers marshal them to their own thread. After a lost connection the client waits 1 s
/// before reconnecting, and doubles the wait after every failed attempt, up to 30 s.
/// </remarks>
/// <param name="pipeName">The pipe name; tests pass a unique one.</param>
/// <param name="requestTimeout">How long <see cref="SendAsync{TResponse}"/> waits for a response.</param>
public sealed class PipeClient(string pipeName, TimeSpan requestTimeout) : IServiceChannel, IAsyncDisposable
{
    /// <summary>The response timeout the tray uses.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<PipeMessage>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeClientStream? _pipe;
    private Task? _loop;
    private int _nextRequestId;
    private volatile bool _connected;

    /// <summary>Raised with the status the subscription answers with, and with every status push.</summary>
    public event EventHandler<StatusResponse>? StatusReceived;

    /// <summary>Raised with every alert the service pushes.</summary>
    public event EventHandler<Alert>? AlertReceived;

    /// <summary>Raised with true once connected and subscribed, and with false when that connection is lost.</summary>
    public event EventHandler<bool>? ConnectionChanged;

    /// <inheritdoc/>
    public bool IsConnected => _connected;

    /// <summary>Tells whether an exception is one <see cref="SendAsync{TResponse}"/> reports a failed request with.</summary>
    /// <param name="exception">The exception.</param>
    /// <returns>True for a lost connection, a timeout, an error response or an unexpected response.</returns>
    public static bool IsRequestFailure(Exception exception) =>
        exception is IOException or TimeoutException or InvalidOperationException or InvalidDataException;

    /// <summary>Starts connecting; the client keeps reconnecting until it is disposed.</summary>
    /// <exception cref="InvalidOperationException">The client is already started.</exception>
    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The pipe client is already started.");
        }

        _loop = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task<TResponse> SendAsync<TResponse>(PipeMessage request, CancellationToken cancellationToken)
        where TResponse : PipeMessage
    {
        ArgumentNullException.ThrowIfNull(request);
        string requestName = request.GetType().Name;
        NamedPipeClientStream pipe =
            Volatile.Read(ref _pipe) ?? throw new IOException($"Cannot send {requestName}: the FreeSpaceWatcher service is not connected.");
        int id = Interlocked.Increment(ref _nextRequestId);
        TaskCompletionSource<PipeMessage> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await WriteAsync(pipe, request with { RequestId = id }, cancellationToken).ConfigureAwait(false);
            PipeMessage response = await reply.Task.WaitAsync(requestTimeout, cancellationToken).ConfigureAwait(false);
            return response switch
            {
                TResponse typed => typed,
                ErrorResponse error => throw new InvalidOperationException($"The service rejected {requestName}: {error.Message}"),
                _ => throw new InvalidDataException($"The service answered {requestName} with {response.GetType().Name}."),
            };
        }
        catch (TimeoutException ex)
        {
            string seconds = requestTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
            throw new TimeoutException($"The service did not answer {requestName} within {seconds} s.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref _pipe)?.Dispose();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stop.Dispose();
        _writeLock.Dispose();
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, stop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken stop)
    {
        TimeSpan delay = FirstRetryDelay;
        while (!stop.IsCancellationRequested)
        {
            if (await TryRunConnectionAsync(stop).ConfigureAwait(false))
            {
                delay = FirstRetryDelay;
            }

            if (!await DelayAsync(delay, stop).ConfigureAwait(false))
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestRetryDelay.Ticks));
        }
    }

    private async Task<bool> TryRunConnectionAsync(CancellationToken stop)
    {
        NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(ConnectTimeout, stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (ex is UnauthorizedAccessException)
            {
                TrayLog.Warning($"Connecting to pipe '{pipeName}' was refused.", ex);
            }

            return false;
        }

        await RunSessionAsync(pipe, stop).ConfigureAwait(false);
        return true;
    }

    private async Task RunSessionAsync(NamedPipeClientStream pipe, CancellationToken stop)
    {
        Volatile.Write(ref _pipe, pipe);
        Task reading = ReadLoopAsync(pipe, stop);
        try
        {
            StatusResponse status = await SendAsync<StatusResponse>(new SubscribeRequest(), stop).ConfigureAwait(false);
            _connected = true;
            ConnectionChanged?.Invoke(this, true);
            StatusReceived?.Invoke(this, status);
        }
        catch (Exception ex) when (IsRequestFailure(ex) || ex is OperationCanceledException)
        {
            TrayLog.Warning($"Subscribing to pipe '{pipeName}' failed.", ex);
            await pipe.DisposeAsync().ConfigureAwait(false);
        }

        await reading.ConfigureAwait(false);
        Volatile.Write(ref _pipe, null);
        FailPendingRequests();
        await pipe.DisposeAsync().ConfigureAwait(false);
        if (_connected)
        {
            _connected = false;
            ConnectionChanged?.Invoke(this, false);
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken stop)
    {
        try
        {
            using StreamReader reader = new(pipe, Utf8, false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(stop).ConfigureAwait(false) is string line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Dispatch(line);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            TrayLog.Information($"Pipe '{pipeName}' closed: {ex.Message}", null);
        }
    }

    private void Dispatch(string line)
    {
        PipeMessage message;
        try
        {
            message = PipeProtocol.Deserialize(line);
        }
        catch (InvalidDataException ex)
        {
            TrayLog.Warning($"Ignoring a malformed message from pipe '{pipeName}'.", ex);
            return;
        }

        switch (message)
        {
            case StatusPush push:
                StatusReceived?.Invoke(this, push.Status);
                break;
            case AlertPush push:
                AlertReceived?.Invoke(this, push.Alert);
                break;
            default:
                if (_pending.TryRemove(message.RequestId, out TaskCompletionSource<PipeMessage>? reply))
                {
                    reply.TrySetResult(message);
                }
                else
                {
                    TrayLog.Warning($"Ignoring {message.GetType().Name} {message.RequestId}: no request is waiting for it.", null);
                }

                break;
        }
    }

    private async Task WriteAsync(NamedPipeClientStream pipe, PipeMessage message, CancellationToken cancellationToken)
    {
        byte[] line = Utf8.GetBytes(PipeProtocol.Serialize(message) + "\n");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            throw new IOException($"Cannot send {message.GetType().Name}: the connection to the service was closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void FailPendingRequests()
    {
        foreach (int id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<PipeMessage>? reply))
            {
                reply.TrySetException(new IOException("The connection to the FreeSpaceWatcher service was lost."));
            }
        }
    }
}
