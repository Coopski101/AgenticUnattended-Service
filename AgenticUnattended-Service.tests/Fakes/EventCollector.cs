using AgenticUnattended.Events;

namespace AgenticUnattended.Tests.Fakes;

public sealed class EventCollector : IDisposable
{
    private readonly EventBus _bus;
    private readonly System.Threading.Channels.ChannelReader<BeaconEvent> _reader;
    private readonly List<BeaconEvent> _events = [];

    public EventCollector(EventBus bus)
    {
        _bus = bus;
        _reader = bus.Subscribe();
    }

    public IReadOnlyList<BeaconEvent> Events
    {
        get
        {
            Drain();
            return _events;
        }
    }

    public BeaconEvent Last
    {
        get
        {
            Drain();
            return _events[^1];
        }
    }

    private void Drain()
    {
        while (_reader.TryRead(out var evt))
            _events.Add(evt);
    }

    public void Dispose()
    {
        _bus.Unsubscribe(_reader);
    }
}
