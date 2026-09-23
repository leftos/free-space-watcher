using System.IO;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Tray.Pipe;

/// <summary>Sends requests to the service and waits for their responses.</summary>
public interface IServiceChannel
{
    /// <summary>Gets whether the service is connected and the subscription is up.</summary>
    bool IsConnected { get; }

    /// <summary>Sends a request and waits for the response that carries the same request id.</summary>
    /// <typeparam name="TResponse">The response type the request is answered with.</typeparam>
    /// <param name="request">The request; its request id is replaced.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The response.</returns>
    /// <exception cref="IOException">The service is not connected, or the connection was lost.</exception>
    /// <exception cref="TimeoutException">The service did not answer in time.</exception>
    /// <exception cref="InvalidOperationException">The service answered with an error.</exception>
    /// <exception cref="InvalidDataException">The service answered with an unexpected message type.</exception>
    Task<TResponse> SendAsync<TResponse>(PipeMessage request, CancellationToken cancellationToken)
        where TResponse : PipeMessage;
}
