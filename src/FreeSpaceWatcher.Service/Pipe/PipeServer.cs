using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FreeSpaceWatcher.Service.Pipe;

/// <summary>Serves the newline-delimited JSON pipe: one task per client, a response per request, pushes for subscribers.</summary>
/// <remarks>
/// The pipe grants full control to SYSTEM, Administrators and the account running the service, and read/write to interactive
/// users. A malformed line gets an <see cref="ErrorResponse"/> and the connection stays up. A client that sends
/// <see cref="SubscribeRequest"/> gets the current status as the response, then every status push, alert push and alerts-changed
/// push.
/// </remarks>
/// <param name="handler">Answers requests.</param>
/// <param name="hub">Supplies status and pushes.</param>
/// <param name="logger">Receives connection and request failures.</param>
/// <param name="pipeName">The pipe name; tests pass a unique one.</param>
/// <param name="firstInstance">The pipe's first instance, from <see cref="ClaimFirstInstance"/>; the server owns and disposes it.</param>
public sealed partial class PipeServer(
    PipeRequestHandler handler,
    StatusHub hub,
    ILogger<PipeServer> logger,
    string pipeName,
    NamedPipeServerStream firstInstance
) : BackgroundService
{
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LongestRetryDelay = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private NamedPipeServerStream? _pending = firstInstance;
    private int _nextClientId;

    /// <summary>
    /// Creates the pipe's first instance, so that a second service instance finds the pipe taken before it touches anything
    /// else; works for any account, elevated or not.
    /// </summary>
    /// <param name="pipeName">The pipe name.</param>
    /// <returns>The first instance, for the <see cref="PipeServer"/> constructor.</returns>
    /// <exception cref="PipeNameInUseException">Another process already serves the pipe.</exception>
    public static NamedPipeServerStream ClaimFirstInstance(string pipeName)
    {
        try
        {
            return CreateInstance(pipeName, firstInstance: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new PipeNameInUseException(
                $"Another process already serves the pipe '{pipeName}' ({ex.Message}). Stop the other FreeSpaceWatcher instance first.",
                ex
            );
        }
    }

    /// <summary>Starts the accept loop on the claimed first instance.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>A task that completes when the accept loop has started.</returns>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LogListening(logger, pipeName);
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        Interlocked.Exchange(ref _pending, null)?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan retryDelay = FirstRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = await AcceptAsync(stoppingToken).ConfigureAwait(false);
            if (pipe is not null)
            {
                retryDelay = FirstRetryDelay;
                int id = Interlocked.Increment(ref _nextClientId);
                _clients[id] = Task.Run(() => ServeClientAsync(id, pipe, stoppingToken), CancellationToken.None);
                continue;
            }

            if (!await DelayAsync(retryDelay, stoppingToken).ConfigureAwait(false))
            {
                break;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, LongestRetryDelay.Ticks));
        }

        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static PipeSecurity CreateSecurity()
    {
        PipeSecurity security = new();
        security.AddAccessRule(Allow(WellKnownSidType.LocalSystemSid, PipeAccessRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.BuiltinAdministratorsSid, PipeAccessRights.FullControl));
        security.AddAccessRule(Allow(WellKnownSidType.InteractiveSid, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize));
        using var current = WindowsIdentity.GetCurrent();
        if (current.User is SecurityIdentifier user)
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    private static PipeAccessRule Allow(WellKnownSidType sid, PipeAccessRights rights) =>
        new(new SecurityIdentifier(sid, null), rights, AccessControlType.Allow);

    private static NamedPipeServerStream CreateInstance(string pipeName, bool firstInstance) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0,
            0,
            CreateSecurity()
        );

    private async Task<NamedPipeServerStream?> AcceptAsync(CancellationToken stoppingToken)
    {
        NamedPipeServerStream? pipe = null;
        try
        {
            pipe = Interlocked.Exchange(ref _pending, null) ?? CreateInstance(pipeName, firstInstance: false);
            await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            return pipe;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            pipe?.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            pipe?.Dispose();
            LogAcceptFailed(logger, ex);
            return null;
        }
    }

    private async Task ServeClientAsync(int id, NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        LogClientConnected(logger, id);
        PipeConnection connection = new(pipe);
        Task pushes = connection.PumpPushesAsync(stoppingToken);
        IDisposable? subscription = null;
        try
        {
            using StreamReader reader = new(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (await reader.ReadLineAsync(stoppingToken).ConfigureAwait(false) is string line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                PipeMessage response = Respond(id, line, pipe, connection, ref subscription);
                await connection.SendAsync(response, stoppingToken).ConfigureAwait(false);
            }

            LogClientGone(logger, id, "it closed the connection");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            LogClientGone(logger, id, ex.Message);
        }
        finally
        {
            subscription?.Dispose();
            connection.CompletePushes();
            await DrainAsync(id, pushes).ConfigureAwait(false);
            connection.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            _clients.TryRemove(id, out _);
        }
    }

    private PipeMessage Respond(int id, string line, NamedPipeServerStream pipe, PipeConnection connection, ref IDisposable? subscription)
    {
        PipeMessage request;
        try
        {
            request = PipeProtocol.Deserialize(line);
        }
        catch (InvalidDataException ex)
        {
            LogMalformed(logger, ex.Message);
            return new ErrorResponse(ex.Message);
        }

        if (request is SubscribeRequest)
        {
            LogSubscribe(logger, id);
            subscription ??= hub.Subscribe(connection.EnqueuePush);
            return hub.Current with { RequestId = request.RequestId };
        }

        try
        {
            return handler.Handle(request, work => pipe.RunAsClient(() => work()));
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            LogRequestFailed(logger, ex, request.GetType().Name);
            return new ErrorResponse($"The service could not handle {request.GetType().Name}: {ex.Message}") { RequestId = request.RequestId };
        }
    }

    private async Task DrainAsync(int id, Task pushes)
    {
        try
        {
            await pushes.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            LogClientGone(logger, id, "push stopped: " + ex.Message);
        }
    }

    [LoggerMessage(EventId = 1401, Level = LogLevel.Information, Message = "Listening on pipe {PipeName}")]
    private static partial void LogListening(ILogger logger, string pipeName);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Warning, Message = "Accepting a pipe client failed")]
    private static partial void LogAcceptFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Debug, Message = "Pipe client {ClientId} connected")]
    private static partial void LogClientConnected(ILogger logger, int clientId);

    [LoggerMessage(EventId = 1404, Level = LogLevel.Debug, Message = "Pipe client {ClientId} sent SubscribeRequest")]
    private static partial void LogSubscribe(ILogger logger, int clientId);

    [LoggerMessage(EventId = 1405, Level = LogLevel.Debug, Message = "Pipe client {ClientId} disconnected: {Reason}")]
    private static partial void LogClientGone(ILogger logger, int clientId, string reason);

    [LoggerMessage(EventId = 1406, Level = LogLevel.Debug, Message = "Malformed pipe message: {Error}")]
    private static partial void LogMalformed(ILogger logger, string error);

    [LoggerMessage(EventId = 1407, Level = LogLevel.Warning, Message = "Handling {RequestType} failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception, string requestType);
}
