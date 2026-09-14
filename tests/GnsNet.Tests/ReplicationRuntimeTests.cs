namespace GnsNet.Tests;

using GnsNet;
using Xunit;
using MemoryPack;

public sealed partial class ReplicationRuntimeTests
{
    [MemoryPackable] private partial record RenameCommand(string Name);
    [MemoryPackable] private partial record RenameReply(bool Accepted, string Name);
    [Fact]
    public void Registry_EmitsStableLifecycleAndOwnershipChanges()
    {
        var registry = new NetworkObjectRegistry<string>(); var changes = new List<NetworkObjectChange>(); registry.Changed += changes.Add;
        NetworkObjectDescriptor spawned = registry.Spawn(7, "alice", 10);
        Assert.True(registry.TransferOwnership(spawned.ObjectId, "bob"));
        Assert.Equal(new[] { new NetworkObjectChange(NetworkObjectChangeKind.Spawned, spawned with { OwnerId = "bob" }) }, registry.SnapshotChanges());
        Assert.True(registry.Despawn(spawned.ObjectId, "match-ended"));
        Assert.Equal(new[] { NetworkObjectChangeKind.Spawned, NetworkObjectChangeKind.OwnershipTransferred, NetworkObjectChangeKind.Despawned }, changes.Select(x => x.Kind));
        Assert.Equal("bob", changes[1].Object.OwnerId); Assert.Equal("alice", changes[1].PreviousOwner);
    }

    [Fact]
    public void RpcRouter_DescribesStableEndpointCapabilities()
    {
        var router = new RpcRouter();
        router.Register("z-last", RpcAuthority.ServerOnly, request => new(request.RequestId, true, null));
        router.Register("a-first", RpcAuthority.AnyAuthenticated, request => new(request.RequestId, true, null));

        Assert.Equal(new[]
        {
            new RpcEndpointCapability("a-first", RpcAuthority.AnyAuthenticated),
            new RpcEndpointCapability("z-last", RpcAuthority.ServerOnly)
        }, router.DescribeCapabilities());
        Assert.True(router.TryGetCapability("a-first", out RpcEndpointCapability capability));
        Assert.Equal(RpcAuthority.AnyAuthenticated, capability.Authority);
        Assert.False(router.TryGetCapability("missing", out _));
    }

    [Fact]
    public void RoomLifecycle_EmitsDeterministicSceneTransitions()
    {
        var room = new RoomLifecycle<string>(); var transitions = new List<RoomSceneTransition>(); room.SceneChanged += transitions.Add;
        Assert.False(room.TransitionScene("default", 1));
        Assert.True(room.TransitionScene("arena", 42));
        Assert.Equal("arena", room.CurrentScene);
        Assert.Equal(new[] { new RoomSceneTransition("default", "arena", 42) }, transitions);
        room.Ended(); Assert.False(room.TransitionScene("results", 43));
    }

    [Fact]
    public void RoomSessionCoordinator_IntegratesMembershipReadinessScenesAndLateJoinState()
    {
        var room = new RoomLifecycle<string>(3) { AllowLateJoin = true };
        var objects = new NetworkObjectRegistry<string>(); NetworkObjectDescriptor entity = objects.Spawn(7, "owner", 4);
        var coordinator = new RoomSessionCoordinator<string>(room, objects); var events = new List<RoomLifecycleEvent<string>>(); coordinator.LifecycleChanged += events.Add;
        Assert.True(coordinator.Join("owner")); Assert.True(coordinator.SetReady("owner", true));
        Assert.True(coordinator.Start()); coordinator.BeginGame(); Assert.True(coordinator.TransitionScene("arena", 9));
        Assert.True(coordinator.Join("late")); RoomStateSnapshot<string> snapshot = coordinator.GetLateJoinState("late");
        Assert.Equal(RoomPhase.InGame, snapshot.Phase); Assert.Equal("arena", snapshot.Scene); Assert.Contains(entity, snapshot.Objects);
        Assert.Equal(new[] { RoomLifecycleEventKind.Joined, RoomLifecycleEventKind.ReadyChanged, RoomLifecycleEventKind.Started, RoomLifecycleEventKind.GameBegan, RoomLifecycleEventKind.SceneChanged, RoomLifecycleEventKind.Joined }, events.Select(x => x.Kind));
        Assert.True(coordinator.Leave("late")); Assert.Equal(RoomLifecycleEventKind.Left, events[^1].Kind);
    }

    [Fact]
    public void RoomSessionCoordinator_RejectsLateJoinSnapshotBeforeGameAndUnknownClients()
    {
        var coordinator = new RoomSessionCoordinator<string>(new RoomLifecycle<string>(), new NetworkObjectRegistry<string>());
        Assert.True(coordinator.Join("p")); Assert.Throws<InvalidOperationException>(() => coordinator.GetLateJoinState("p"));
        Assert.Throws<InvalidOperationException>(() => coordinator.GetLateJoinState("unknown"));
    }

    [Fact]
    public void RoomLifecycle_EmitsAuthoritativeEventsForDrainAndEndOnlyOnce()
    {
        var room = new RoomLifecycle<string>(); var events = new List<RoomLifecycleEvent<string>>(); room.LifecycleChanged += events.Add;
        room.Drain(); room.Drain(); room.Ended(); room.Ended();
        Assert.Equal(new[] { RoomLifecycleEventKind.Draining, RoomLifecycleEventKind.Ended }, events.Select(x => x.Kind));
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
    public void RpcRouter_TypedCommandDeserializesAndSerializesResponse()
    {
        var router = new RpcRouter(); router.RegisterCommand<RenameCommand, RenameReply>("rename", RpcAuthority.AnyAuthenticated, (_, command) => new(true, command.Name));
        Guid id = Guid.NewGuid(); RpcResponse response = router.Dispatch(new(id, "rename", null, "client", NetSerializer.Serialize(new RenameCommand("new-name")), 1));
        Assert.True(response.Accepted); Assert.Equal(new RenameReply(true, "new-name"), NetSerializer.Deserialize<RenameReply>(response.Payload!));
        Assert.False(router.Dispatch(new(id, "rename", null, "client", [1, 2], 1)).Accepted);
    }

    [Fact]
    public void RpcTransportEnvelopes_RoundTripAndPreserveCorrelation()
    {
        Guid requestId = Guid.NewGuid(); var request = new RpcRequestEnvelope(requestId, "rename", 12, [4, 5]);
        RpcRequestEnvelope decodedRequest = NetSerializer.Deserialize<RpcRequestEnvelope>(NetSerializer.Serialize(request))!;
        Assert.Equal(request.RequestId, decodedRequest.RequestId); Assert.Equal(request.Endpoint, decodedRequest.Endpoint); Assert.Equal(request.ObjectId, decodedRequest.ObjectId); Assert.Equal(request.Payload, decodedRequest.Payload);
        var response = new RpcResponseEnvelope(requestId, false, null, "denied");
        RpcResponseEnvelope decodedResponse = NetSerializer.Deserialize<RpcResponseEnvelope>(NetSerializer.Serialize(response))!;
        Assert.Equal(response.RequestId, decodedResponse.RequestId); Assert.Equal(response.Accepted, decodedResponse.Accepted); Assert.Equal(response.Error, decodedResponse.Error); Assert.Null(decodedResponse.Payload);
    }

    [Fact]
    public void ClientRpcRouter_DispatchesOnlyMatchingTargetInvocation()
    {
        var router = new ClientRpcRouter(); string? received = null; uint receivedTick = 0;
        router.Register<RenameCommand>(42, "client.rename", (request, command, tick) => { received = command.Name; receivedTick = tick; });
        var invocation = new RpcRequestEnvelope(Guid.NewGuid(), "client.rename", null, NetSerializer.Serialize(new RenameCommand("target-only")));
        Assert.True(router.Dispatch(42, 99, invocation)); Assert.Equal("target-only", received); Assert.Equal((uint)99, receivedTick);
        Assert.False(router.Dispatch(42, 100, invocation with { Endpoint = "other" }));
        Assert.False(router.Dispatch(43, 100, invocation));
    }

    [Fact]
    public void ClientRpcRouter_RejectsMalformedTargetInvocation()
    {
        var router = new ClientRpcRouter(); router.Register<RenameCommand>(42, "client.rename", (_, _, _) => { });
        Assert.False(router.Dispatch(42, 1, new RpcRequestEnvelope(Guid.NewGuid(), "client.rename", null, [1, 2, 3])));
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
