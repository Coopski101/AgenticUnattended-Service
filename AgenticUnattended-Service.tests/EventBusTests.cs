using AgenticUnattended.Events;

namespace AgenticUnattended.Tests;

public sealed class EventBusTests
{
    private static BeaconEvent MakeEvent(BeaconEventType type = BeaconEventType.Done) =>
        new()
        {
            EventType = type,
            SessionId = "test",
            Source = AgentSource.Copilot,
            HookEvent = "Test",
            Reason = "test reason",
        };

    [Fact]
    public void Publish_DeliversToSubscriber()
    {
        var bus = new EventBus();
        var reader = bus.Subscribe();

        bus.Publish(MakeEvent());

        Assert.True(reader.TryRead(out var evt));
        Assert.Equal(BeaconEventType.Done, evt!.EventType);
    }

    [Fact]
    public void Publish_DeliversToMultipleSubscribers()
    {
        var bus = new EventBus();
        var r1 = bus.Subscribe();
        var r2 = bus.Subscribe();

        bus.Publish(MakeEvent());

        Assert.True(r1.TryRead(out _));
        Assert.True(r2.TryRead(out _));
    }

    [Fact]
    public void Unsubscribe_StopsDelivery()
    {
        var bus = new EventBus();
        var reader = bus.Subscribe();
        bus.Unsubscribe(reader);

        bus.Publish(MakeEvent());

        Assert.False(reader.TryRead(out _));
    }

    [Fact]
    public void Publish_MultipleEvents_MaintainsOrder()
    {
        var bus = new EventBus();
        var reader = bus.Subscribe();

        bus.Publish(MakeEvent(BeaconEventType.Waiting));
        bus.Publish(MakeEvent(BeaconEventType.Done));
        bus.Publish(MakeEvent(BeaconEventType.Clear));

        Assert.True(reader.TryRead(out var e1));
        Assert.True(reader.TryRead(out var e2));
        Assert.True(reader.TryRead(out var e3));
        Assert.Equal(BeaconEventType.Waiting, e1!.EventType);
        Assert.Equal(BeaconEventType.Done, e2!.EventType);
        Assert.Equal(BeaconEventType.Clear, e3!.EventType);
    }

    [Fact]
    public void Subscribe_DoesNotReceivePriorMessages()
    {
        var bus = new EventBus();
        bus.Publish(MakeEvent());

        var reader = bus.Subscribe();

        Assert.False(reader.TryRead(out _));
    }
}
