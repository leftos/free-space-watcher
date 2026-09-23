using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;

namespace FreeSpaceWatcher.Tray.Instance;

/// <summary>
/// Lets a second launch of the tray hand its <c>--open</c> window to the tray already running: a named pipe owned by the running
/// tray, one per Windows session and restricted to the current user, that takes one window name per connection.
/// </summary>
public sealed class TrayInstanceChannel : IAsyncDisposable
{
    private const int MaxLineLength = 64;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private TrayInstanceChannel(string pipeName, Action<TrayWindow> onOpen, ITrayLog log) =>
        _loop = Task.Run(() => ListenAsync(pipeName, onOpen, log, _stop.Token));

    /// <summary>Gets this session's pipe name, <c>FreeSpaceWatcher.Tray.&lt;session id&gt;</c>.</summary>
    public static string SessionPipeName { get; } = CreateSessionPipeName();

    /// <summary>Starts listening for windows to open.</summary>
    /// <param name="pipeName">The pipe name; the app passes <see cref="SessionPipeName"/>.</param>
    /// <param name="onOpen">Receives each window a second launch asked for, on a thread-pool thread.</param>
    /// <param name="log">Receives connections that failed and names that are not windows.</param>
    /// <returns>The channel; dispose it to stop listening.</returns>
    public static TrayInstanceChannel Listen(string pipeName, Action<TrayWindow> onOpen, ITrayLog log) => new(pipeName, onOpen, log);

    /// <summary>Asks the running tray to open a window.</summary>
    /// <param name="pipeName">The pipe name the running tray listens on.</param>
    /// <param name="window">The window.</param>
    /// <param name="log">Receives the failure, if the running tray could not be reached.</param>
    /// <returns>Whether the request was delivered.</returns>
    public static bool TrySend(string pipeName, TrayWindow window, ITrayLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            using NamedPipeClientStream client = new(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeout);
            byte[] line = Encoding.UTF8.GetBytes(window.ToString().ToLowerInvariant() + "\n");
            client.Write(line);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            log.Warning($"Could not ask the running tray to open {window}.", ex);
            return false;
        }
    }

    /// <summary>Stops listening.</summary>
    /// <returns>A task that completes when the listener has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

    private static async Task ListenAsync(string pipeName, Action<TrayWindow> onOpen, ITrayLog log, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await AcceptOneAsync(pipeName, onOpen, log, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warning($"Another user owns the pipe {pipeName}; a second launch cannot open windows in this tray.", ex);
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                log.Warning("A second launch's request to open a window failed.", ex);
                await DelayAsync(token);
            }
        }
    }

    private static async Task AcceptOneAsync(string pipeName, Action<TrayWindow> onOpen, ITrayLog log, CancellationToken token)
    {
        await using NamedPipeServerStream server = new(
            pipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
        );
        await server.WaitForConnectionAsync(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(ReadTimeout);
        using StreamReader reader = new(server, new UTF8Encoding(false), false, MaxLineLength, leaveOpen: true);
        string? line = await reader.ReadLineAsync(timeout.Token);
        if (TrayArguments.TryParseWindow(line?.Trim(), out TrayWindow window))
        {
            log.Information($"A second launch asked to open {window}.", null);
            onOpen(window);
        }
        else
        {
            log.Warning($"A second launch sent '{line}', which is not a window; ignored.", null);
        }
    }

    private static string CreateSessionPipeName()
    {
        using var self = Process.GetCurrentProcess();
        return string.Create(CultureInfo.InvariantCulture, $"FreeSpaceWatcher.Tray.{self.SessionId}");
    }

    // A cancelled delay means the channel is stopping, which the loop's condition sees.
    private static async Task DelayAsync(CancellationToken token) =>
        await Task.Delay(RetryDelay, token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
