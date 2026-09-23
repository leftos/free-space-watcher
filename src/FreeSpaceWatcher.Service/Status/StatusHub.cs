using FreeSpaceWatcher.Core.Alerts;
using FreeSpaceWatcher.Core.Ipc;

namespace FreeSpaceWatcher.Service.Status;

/// <summary>Holds the latest status snapshot and pushes status and alerts to subscribed pipe clients.</summary>
public sealed class StatusHub
{
    private readonly Lock _gate = new();
    private readonly List<Action<PipeMessage>> _subscribers = [];
    private StatusResponse _current = new([], false, null);

    /// <summary>Gets the latest status snapshot; empty until the sampler publishes one.</summary>
    public StatusResponse Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Stores a status snapshot and pushes it to every subscriber as a <see cref="StatusPush"/>.</summary>
    /// <param name="status">The snapshot.</param>
    public void PublishStatus(StatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            _current = status;
        }

        Push(new StatusPush(status));
    }

    /// <summary>Pushes a new alert to every subscriber as an <see cref="AlertPush"/>.</summary>
    /// <param name="alert">The alert.</param>
    public void PublishAlert(Alert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Push(new AlertPush(alert));
    }

    /// <summary>Tells every subscriber, with an <see cref="AlertsChangedPush"/>, that alerts were acknowledged or deleted.</summary>
    public void PublishAlertsChanged() => Push(new AlertsChangedPush());

    /// <summary>Starts delivering pushes to <paramref name="onPush"/>, which must not block.</summary>
    /// <param name="onPush">Receives each push on the publishing thread.</param>
    /// <returns>A handle that stops the delivery when disposed.</returns>
    public IDisposable Subscribe(Action<PipeMessage> onPush)
    {
        ArgumentNullException.ThrowIfNull(onPush);
        lock (_gate)
        {
            _subscribers.Add(onPush);
        }

        return new Subscription(this, onPush);
    }

    private void Push(PipeMessage message)
    {
        Action<PipeMessage>[] targets;
        lock (_gate)
        {
            targets = [.. _subscribers];
        }

        foreach (Action<PipeMessage> target in targets)
        {
            target(message);
        }
    }

    private void Unsubscribe(Action<PipeMessage> onPush)
    {
        lock (_gate)
        {
            _subscribers.Remove(onPush);
        }
    }

    private sealed class Subscription(StatusHub hub, Action<PipeMessage> onPush) : IDisposable
    {
        public void Dispose() => hub.Unsubscribe(onPush);
    }
}
