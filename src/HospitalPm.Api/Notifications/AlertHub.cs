using System.Collections.Concurrent;
using System.Threading.Channels;

namespace HospitalPm.Api.Notifications;

/// <summary>
/// A new service request, as the biomedical engineers and the head see it pop up: enough to know what it is and where, and to
/// open it. Nothing here is about a patient; the fault text is the reporter's own words about the machine.
/// </summary>
public sealed record ServiceRequestAlert(
    int Id,
    string Number,
    int EquipmentId,
    string? AssetTag,
    string? Machine,
    string? Location,
    string Priority,
    string Fault,
    string ReportedBy,
    DateTime ReportedAtUtc);

/// <summary>
/// Hands a new service request to everyone who is looking at the screen right now, with no outside service: a signed-in
/// engineer's browser keeps one stream open to this process (see <see cref="AlertEndpoints"/>), and the request that was just
/// written is pushed down it. It is in-process on purpose. The installation is one program on one machine and must run with no
/// internet and no message broker, so there is nothing to install or to keep running beside it.
///
/// Alerts are not stored. Someone who was not signed in when a request was placed sees it in the Request Service list, which is
/// the record; the pop-up is only the nudge.
/// </summary>
public sealed class AlertHub
{
    /// <summary>The most a slow or stalled browser is allowed to fall behind by. The oldest are dropped, never the newest.</summary>
    private const int Backlog = 50;

    private readonly ConcurrentDictionary<Guid, Channel<ServiceRequestAlert>> _subscribers = new();

    /// <summary>People listening right now.</summary>
    public int Listeners => _subscribers.Count;

    /// <summary>Starts listening. Dispose the result to stop, which is what closing the page does.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ServiceRequestAlert>(new BoundedChannelOptions(Backlog)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var id = Guid.NewGuid();
        _subscribers[id] = channel;
        return new Subscription(this, id, channel.Reader);
    }

    /// <summary>Tells everyone who is listening. Never waits and never fails: a request must not be held up by a screen.</summary>
    public void Publish(ServiceRequestAlert alert)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(alert);
        }
    }

    private void Stop(Guid id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public sealed class Subscription(AlertHub hub, Guid id, ChannelReader<ServiceRequestAlert> reader) : IDisposable
    {
        public ChannelReader<ServiceRequestAlert> Reader { get; } = reader;

        public void Dispose() => hub.Stop(id);
    }
}
