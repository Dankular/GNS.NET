namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class ReplicationV2Tests
{
    [Fact]
    public void PacketV2_RoundTripsRecordsAndRejectsBoundViolations()
    {
        var mask = new DirtyFieldMask(3); mask.Set(1);
        var packet = new ReplicationPacketV2(9, 10, 8, 7,
        [new(EntityRecordKind.Spawn, new(42), new(3), 0, 0, null, true, [1]),
         new(EntityRecordKind.DeltaComponent, new(42), default, 12, 2, mask, true, [2, 3])]);
        ReplicationPacketV2 decoded = ReplicationPacketV2.Decode(packet.Encode());
        Assert.Equal(packet.ServerTick, decoded.ServerTick);
        Assert.Equal(2, decoded.Records.Count);
        Assert.True(decoded.Records[1].DirtyMask!.IsSet(1));
        Assert.Throws<InvalidDataException>(() => ReplicationPacketV2.Decode(packet.Encode(), new ReplicationPacketV2Limits { MaximumPacketBytes = 8 }));
    }

    [Fact]
    public void Scheduler_RetainsUnacknowledgedBaselinesAndOrdersLifecycleReliably()
    {
        var world = new ReplicationWorld<string>();
        var scheduler = new ComponentSnapshotScheduler<string>(world, 39);
        scheduler.AddClient("a");
        scheduler.ConfigureVisibility(_ => [new NetworkEntityId(1)]);
        world.Spawn(new NetworkEntityId(1), new ReplicationArchetypeId(4));
        world.SetRaw(new NetworkEntityId(1), 2, 1, [9], required: true);

        scheduler.Tick(1);
        var first = scheduler.Drain("a", new ReplicationBudget(4096, 8));
        var lifecycle = Assert.Single(first);
        Assert.Equal(NetChannel.Event, lifecycle.Channel);
        ReplicationPacketV2 initial = ReplicationPacketV2.Decode(lifecycle.Frame.Payload);
        Assert.Equal(EntityRecordKind.Spawn, initial.Records[0].Kind);
        Assert.Contains(initial.Records, record => record.Kind == EntityRecordKind.FullComponent);

        world.SetRaw(new NetworkEntityId(1), 2, 1, [10], required: true);
        scheduler.Tick(2);
        Assert.Empty(scheduler.Drain("a", new ReplicationBudget(4096, 8)));
        scheduler.Acknowledge("a", new SnapshotAck(initial.SnapshotSequence));
        scheduler.Tick(3);
        var update = Assert.Single(scheduler.Drain("a", new ReplicationBudget(4096, 8)));
        Assert.Equal(NetChannel.State, update.Channel);
        Assert.Contains(ReplicationPacketV2.Decode(update.Frame.Payload).Records, record => record.Kind == EntityRecordKind.FullComponent);
    }

    [Fact]
    public void Scheduler_DoesNotAdvanceBaselineWhenBudgetDefersState()
    {
        var world = new ReplicationWorld<string>(); var scheduler = new ComponentSnapshotScheduler<string>(world, 40);
        scheduler.AddClient("a"); scheduler.ConfigureVisibility(_ => [new NetworkEntityId(2)]);
        world.Spawn(new NetworkEntityId(2), new ReplicationArchetypeId(1)); world.SetRaw(new NetworkEntityId(2), 1, 1, [1]);
        scheduler.Tick(1); var initial = Assert.Single(scheduler.Drain("a", new ReplicationBudget(4096, 8)));
        scheduler.Acknowledge("a", new SnapshotAck(ReplicationPacketV2.Decode(initial.Frame.Payload).SnapshotSequence));
        world.SetRaw(new NetworkEntityId(2), 1, 1, [2]); scheduler.Tick(2);
        Assert.Empty(scheduler.Drain("a", new ReplicationBudget(1, 1)));
        Assert.Equal(1, scheduler.DeferredRecords);
        var sent = Assert.Single(scheduler.Drain("a", new ReplicationBudget(4096, 8)));
        Assert.Contains(ReplicationPacketV2.Decode(sent.Frame.Payload).Records, record => record.Payload.SequenceEqual(new byte[] { 2 }));
    }
}
