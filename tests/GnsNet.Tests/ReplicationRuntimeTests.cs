namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class ReplicationRuntimeTests
{
    [Fact]
    public void Registry_EmitsStableLifecycleAndOwnershipChanges()
    {
        var registry = new NetworkObjectRegistry<string>(); var changes = new List<NetworkObjectChange>(); registry.Changed += changes.Add;
        NetworkObjectDescriptor spawned = registry.Spawn(7, "alice", 10);
        Assert.True(registry.TransferOwnership(spawned.ObjectId, "bob"));
        Assert.True(registry.Despawn(spawned.ObjectId, "match-ended"));
        Assert.Equal(new[] { NetworkObjectChangeKind.Spawned, NetworkObjectChangeKind.OwnershipTransferred, NetworkObjectChangeKind.Despawned }, changes.Select(x => x.Kind));
        Assert.Equal("bob", changes[1].Object.OwnerId); Assert.Equal("alice", changes[1].PreviousOwner);
    }

    [Fact]
    public void RpcRouter_EnforcesAuthorityAndCorrelatesResponse()
    {
        var router = new RpcRouter(); router.SetOwner(4, "alice");
        router.Register("object.rename", RpcAuthority.OwnerOnly, request => new(request.RequestId, true, request.Payload));
        Guid id = Guid.NewGuid();
        Assert.False(router.Dispatch(new(id, "object.rename", 4, "bob", [1], 2)).Accepted);
        RpcResponse response = router.Dispatch(new(id, "object.rename", 4, "alice", [2], 3));
        Assert.True(response.Accepted); Assert.Equal(id, response.RequestId); Assert.Equal(new byte[] { 2 }, response.Payload);
    }

    [Fact]
    public void Registry_ClientApply_IsIdempotentAndPreservesAuthoritativeIds()
    {
        var server = new NetworkObjectRegistry<string>(); NetworkObjectDescriptor spawned = server.Spawn(2, "a", 4); var client = new NetworkObjectRegistry<string>();
        NetworkObjectChange change = new(NetworkObjectChangeKind.Spawned, spawned); Assert.True(client.Apply(change)); Assert.True(client.Apply(change)); Assert.True(client.TryGet(spawned.ObjectId, out _));
        Assert.True(client.Apply(new NetworkObjectChange(NetworkObjectChangeKind.Despawned, spawned))); Assert.True(client.Apply(new NetworkObjectChange(NetworkObjectChangeKind.Despawned, spawned)));
    }

    [Fact]
    public void Room_RequiresAllMembersReadyBeforeStartAndRejectsLateJoin()
    {
        var room = new RoomLifecycle<string>(2); Assert.True(room.Join("a")); Assert.True(room.Join("b"));
        Assert.True(room.SetReady("a", true)); Assert.Equal(RoomPhase.Lobby, room.Phase); Assert.True(room.SetReady("b", true));
        Assert.Equal(RoomPhase.Ready, room.Phase); Assert.True(room.Start()); room.BeginGame(); Assert.False(room.Join("c"));
    }

    [Fact]
    public async Task RpcRequestTracker_CompletesAndCancelsCorrelatedRequests()
    {
        var tracker = new RpcRequestTracker(); var request = tracker.Create();
        Assert.True(tracker.Complete(new RpcResponse(request.RequestId, true, [9])));
        Assert.True((await request.Completion).Accepted); Assert.Equal(0, tracker.PendingCount);
        using var cts = new CancellationTokenSource(); var cancelled = tracker.Create(cts.Token); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Completion); Assert.Equal(0, tracker.PendingCount);
        var timed = tracker.Create(TimeSpan.FromMilliseconds(10)); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => timed.Completion); Assert.Equal(0, tracker.PendingCount);
    }
}
