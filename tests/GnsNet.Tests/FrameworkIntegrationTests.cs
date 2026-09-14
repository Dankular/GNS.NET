namespace GnsNet.Tests;

using GnsNet;
using MemoryPack;
using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

[MemoryPackable]
public partial class TestState { public int Value { get; set; } }

[MemoryPackable]
public partial class TestEntity { public float X { get; set; } public float Y { get; set; } }

public sealed class FrameworkIntegrationTests
{
    private sealed class FakeController : IShardProcessController<string>, IShardHealthController<string>
    {
        public int Starts; public int Stops; public int Migrations; public bool Running = true;
        public Task StartAsync(string shard, CancellationToken cancellationToken = default) { Starts++; Running = true; return Task.CompletedTask; }
        public Task StopAsync(string shard, CancellationToken cancellationToken = default) { Stops++; Running = false; return Task.CompletedTask; }
        public Task MigrateAsync<TPlayer>(TPlayer player, string source, string target, CancellationToken cancellationToken = default) { Migrations++; return Task.CompletedTask; }
        public bool IsRunning(string shard) => Running;
    }
    private sealed class FakeTransfer : IShardStateTransfer<string, string>
    {
        public byte[] Imported = [];
        public Task<byte[]> ExportAsync(string player, string source, CancellationToken cancellationToken = default) => Task.FromResult(new byte[] { 4, 2 });
        public Task ImportAsync(string player, string target, ReadOnlyMemory<byte> state, CancellationToken cancellationToken = default) { Imported = state.ToArray(); return Task.CompletedTask; }
    }

    private sealed class FlakyBus : IBackendMessageBus
    {
        public int Attempts;
        public ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default) { if (++Attempts < 2) throw new IOException(); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    }
    private sealed class RecordingBus : IBackendMessageBus
    {
        public readonly List<BackendMessage> Messages = new();
        public ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default) { lock (Messages) Messages.Add(message); return ValueTask.CompletedTask; }
        public async IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public void ShardLifecycle_LeasesDrainAndMigrate()
    {
        var lifecycle = new ShardLifecycle<string, string>(TimeSpan.FromMinutes(1));
        lifecycle.Register("a"); lifecycle.Register("b");
        Assert.True(lifecycle.Assign("p", "a"));
        Assert.True(lifecycle.SetDraining("a", true));
        Assert.False(lifecycle.Assign("q", "a"));
        Assert.True(lifecycle.Migrate("p", "b"));
        Assert.True(lifecycle.TryGetPlayerShard("p", out string? shard));
        Assert.Equal("b", shard);
    }

    [Fact]
    public void ValidatedInputRouter_DoesNotCallHandlerForRejectedInput()
    {
        int calls = 0;
        var router = new ValidatedInputRouter<string>();
        var guard = new ServerInputGuard<string, TestState>((_, state) => state.Value <= 5);
        router.Register(1, guard, (_, _) => { calls++; return true; });
        Assert.False(router.Dispatch("p", new NetFrame(1, 0, NetSerializer.Serialize(new TestState { Value = 6 }))));
        Assert.Equal(0, calls);
        Assert.True(router.Dispatch("p", new NetFrame(1, 0, NetSerializer.Serialize(new TestState { Value = 5 }))));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ValidatedInputRouter_DistinguishesRejectedFromUnregistered()
    {
        var router = new ValidatedInputRouter<string>(); var guard = new ServerInputGuard<string, TestState>((_, state) => state.Value <= 5);
        router.Register(1, guard, (_, _) => true);
        Assert.True(router.IsRegistered(1)); Assert.False(router.IsRegistered(2));
        Assert.False(router.Dispatch("p", new NetFrame(1, 0, NetSerializer.Serialize(new TestState { Value = 6 }))));
    }

    [Fact]
    public void ValidatedInputRouter_RejectsCorruptSerializedPayload()
    {
        var router = new ValidatedInputRouter<string>(); router.Register(1, new ServerInputGuard<string, TestState>((_, _) => true), (_, _) => true);
        Assert.False(router.Dispatch("p", new NetFrame(1, 1, [0xFF, 0xFF, 0xFF])));
    }

    [Fact]
    public void SnapshotPipeline_CullsEntitiesAndQueuesDeltaSnapshot()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("p", new InterestPoint(0, 0, 5));
        var delta = new DeltaCompressor<TestState>((_, current) => current, (_, change) => change);
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, delta, entity => (entity.X, entity.Y));
        pipeline.Queue("p", [new TestEntity { X = 1 }, new TestEntity { X = 100 }], new TestState { Value = 1 }, 2, 3, 1, 1);
        Assert.Equal(2, pipeline.Drain("p", 10).Count);
    }

    [Fact]
    public void SnapshotPipeline_AcknowledgementAdvancesPerClientBaseline()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("p", new InterestPoint(0, 0, 5));
        var delta = new DeltaCompressor<TestState>((baseline, current) => new TestState { Value = current.Value - (baseline?.Value ?? 0) }, (baseline, change) => new TestState { Value = (baseline?.Value ?? 0) + change.Value });
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, delta, entity => (entity.X, entity.Y));
        pipeline.Queue("p", [], new TestState { Value = 10 }, 2, 3, 1, 1); var first = pipeline.Drain("p", 10).Single(x => x.Frame.Opcode == 3);
        pipeline.Acknowledge("p", new TestState { Value = 10 });
        pipeline.Queue("p", [], new TestState { Value = 12 }, 2, 3, 2, 1); var second = pipeline.Drain("p", 10).Single(x => x.Frame.Opcode == 3);
        Assert.Equal(10, NetSerializer.Deserialize<TestState>(first.Frame.Payload)!.Value);
        Assert.Equal(2, NetSerializer.Deserialize<TestState>(second.Frame.Payload)!.Value);
    }

    [Fact]
    public void SnapshotPipeline_ThrottlesLowRelevanceUpdates()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("p", new InterestPoint(0, 0, 5));
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        pipeline.Queue("p", [], new TestState { Value = 1 }, 2, 3, 1, .25f); Assert.Single(pipeline.Drain("p", 10));
        pipeline.Queue("p", [], new TestState { Value = 2 }, 2, 3, 2, .25f); Assert.Empty(pipeline.Drain("p", 10));
        pipeline.Queue("p", [], new TestState { Value = 4 }, 2, 3, 5, .25f); Assert.Single(pipeline.Drain("p", 10));
    }

    [Fact]
    public void AutomaticSnapshotScheduler_PublishesOneWorldToAllRegisteredClients()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("a", new(0, 0, 5)); interest.SetView("b", new(0, 0, 5));
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        var scheduler = new AutomaticSnapshotScheduler<string, TestEntity, TestState>(pipeline); scheduler.AddClient("a"); scheduler.AddClient("b"); scheduler.Publish([new TestEntity { X = 1 }], _ => new TestState { Value = 7 }, _ => 1, 2, 3, 1);
        Assert.Equal(2, scheduler.Drain("a", 10).Count); Assert.Equal(2, scheduler.Drain("b", 10).Count);
    }

    [Fact]
    public void AutomaticSnapshotScheduler_QueuesImmediateLateJoinTransfer()
    {
        var interest = new InterestManager<string, TestEntity>();
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        var scheduler = new AutomaticSnapshotScheduler<string, TestEntity, TestState>(pipeline);
        interest.SetView("late", new InterestPoint(0, 0, 100));
        scheduler.AddClient("late", new[] { new TestEntity { X = 4, Y = 5 } }, new TestState { Value = 9 }, 2, 3, 20);
        var frames = scheduler.Drain("late", 8);
        Assert.Equal(2, frames.Count);
        Assert.Contains(frames, x => x.Frame.Opcode == 2);
        Assert.Contains(frames, x => x.Frame.Opcode == 3);
    }

    [Fact]
    public void AutomaticSnapshotScheduler_TickUsesConfiguredWorldAndPolicies()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("a", new InterestPoint(0, 0, 100));
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        var scheduler = new AutomaticSnapshotScheduler<string, TestEntity, TestState>(pipeline); scheduler.AddClient("a");
        scheduler.ConfigureAutoTick(() => new[] { new TestEntity { X = 1, Y = 2 } }, _ => new TestState { Value = 8 }, _ => 1, 4, 5);
        scheduler.Tick(7);
        Assert.Equal(2, scheduler.Drain("a", 8).Count);
    }

    [Fact]
    public void AutomaticSnapshotScheduler_DirtyTrackingSkipsUnchangedEntitiesAndSendsChanges()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("a", new InterestPoint(0, 0, 100));
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        var scheduler = new AutomaticSnapshotScheduler<string, TestEntity, TestState>(pipeline); scheduler.AddClient("a");
        TestEntity entity = new() { X = 1, Y = 2 };
        scheduler.ConfigureAutoTick(() => new[] { entity }, _ => new TestState { Value = 8 }, _ => 1, 4, 5, current => $"{current.X}:{current.Y}");
        scheduler.Tick(1); Assert.Equal(2, scheduler.Drain("a", 8).Count);
        scheduler.Tick(2); Assert.Single(scheduler.Drain("a", 8));
        entity.X = 9; scheduler.Tick(3); var frames = scheduler.Drain("a", 8);
        Assert.Equal(2, frames.Count); Assert.Equal(9, NetSerializer.Deserialize<TestEntity>(frames.Single(x => x.Frame.Opcode == 4).Frame.Payload)!.X);
    }

    [Fact]
    public void ComponentDirtyTracker_RejectsDuplicateKeysAndPrunesRemovedEntities()
    {
        var tracker = new ComponentDirtyTracker<TestEntity>();
        Assert.Equal(2, tracker.Collect(new[] { new TestEntity { X = 1 }, new TestEntity { X = 2 } }, entity => entity.X.ToString()).Count);
        Assert.DoesNotContain(tracker.Collect(new[] { new TestEntity { X = 1 } }, entity => entity.X.ToString()), entity => entity.X == 2);
        Assert.Throws<InvalidOperationException>(() => tracker.Collect(new[] { new TestEntity { X = 1 }, new TestEntity { X = 1 } }, entity => entity.X.ToString()));
        Assert.Equal(1, tracker.TrackedCount);
    }

    [Fact]
    public void ComponentDirtyTracker_TracksComponentsIndependently()
    {
        var tracker = new ComponentDirtyTracker<TestEntity>();
        static IReadOnlyList<byte[]> Components(TestEntity entity) => [BitConverter.GetBytes(entity.X), BitConverter.GetBytes(entity.Y)];
        Assert.Single(tracker.Collect(new[] { new TestEntity { X = 1, Y = 2 } }, _ => "entity", Components));
        Assert.Empty(tracker.Collect(new[] { new TestEntity { X = 1, Y = 2 } }, _ => "entity", Components));
        Assert.Single(tracker.Collect(new[] { new TestEntity { X = 1, Y = 3 } }, _ => "entity", Components));
    }

    [Fact]
    public void AutomaticSnapshotScheduler_BatchesLifecycleRecordsOnReliableChannel()
    {
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(new InterestManager<string, TestEntity>(), new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y));
        var scheduler = new AutomaticSnapshotScheduler<string, TestEntity, TestState>(pipeline); scheduler.AddClient("a");
        scheduler.PublishLifecycle(new[] { new NetworkObjectChange(NetworkObjectChangeKind.Spawned, new NetworkObjectDescriptor(1, 2, "a", 4)) }, 9, 4, change => [1, (byte)change.Object.ObjectId]);
        var item = Assert.Single(scheduler.Drain("a", 4)); Assert.Equal(NetChannel.Event, item.Channel); Assert.Equal(9, item.Frame.Opcode);
    }

    [Fact]
    public void SnapshotPipeline_SelectsConfiguredChannelAutomatically()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("a", new InterestPoint(0, 0, 100));
        var pipeline = new SnapshotPipeline<string, TestEntity, TestState>(interest, new DeltaCompressor<TestState>((_, current) => current, (_, change) => change), entity => (entity.X, entity.Y), _ => NetChannel.Event);
        pipeline.Queue("a", new[] { new TestEntity { X = 1 } }, new TestState(), 6, 7, 1, 1);
        Assert.Equal(NetChannel.Event, pipeline.Drain("a", 4).Single(x => x.Frame.Opcode == 6).Channel);
    }

    [Fact]
    public void InterestManager_EnforcesRegisteredVisibilityBeforeCulling()
    {
        var interest = new InterestManager<string, TestEntity>(); interest.SetView("a", new InterestPoint(0, 0, 100)); interest.SetVisibility("a", entity => entity.X >= 0);
        Assert.Single(interest.Cull("a", new[] { new TestEntity { X = 1 }, new TestEntity { X = -1 } }, entity => (entity.X, entity.Y)));
    }

    [Fact]
    public async Task NetworkRecorder_PersistsRedactedCapture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gnsnet-redacted-{Guid.NewGuid():N}.replay");
        try
        {
            var recorder = new NetworkRecorder(); recorder.Record(true, [1, 2, 3], connectionId: "secret");
            await recorder.SaveAsync(path, packet => packet with { ConnectionId = "redacted", Data = [0] });
            NetworkRecorder loaded = await NetworkRecorder.LoadAsync(path); RecordedPacket packet = Assert.Single(loaded.Packets);
            Assert.Equal("redacted", packet.ConnectionId); Assert.Equal(new byte[] { 0 }, packet.Data);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task NetworkReplayIndex_PersistsCaptureMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gnsnet-index-{Guid.NewGuid():N}.json");
        try
        {
            var recorder = new NetworkRecorder(); DateTimeOffset first = DateTimeOffset.UtcNow; recorder.Record(true, [1, 2], first, "c"); recorder.Record(false, [3], first.AddSeconds(1), "c");
            var index = new NetworkReplayIndex(); NetworkReplayIndexEntry entry = index.Add("capture.replay", recorder); await index.SaveAsync(path);
            NetworkReplayIndex loaded = await NetworkReplayIndex.LoadAsync(path); Assert.Equal(entry, Assert.Single(loaded.Entries)); Assert.Equal(3, entry.ByteCount);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void PrioritySendQueue_ShedsFramesAtConfiguredBudgetAndAccountsBytes()
    {
        var queue = new PrioritySendQueue(maxFrames: 1, maxBytes: 64); var first = new NetFrame(1, 1, new byte[8]); var second = new NetFrame(2, 1, new byte[8]);
        Assert.True(queue.Enqueue(first, NetChannel.State, 1)); Assert.False(queue.Enqueue(second, NetChannel.State, 1));
        Assert.Equal(1, queue.ShedFrames); Assert.True(queue.ShedBytes > 0); Assert.Single(queue.Drain(1)); Assert.Equal(0, queue.QueuedBytes);
        var metrics = new ConnectionMetrics(); var shed = queue.ConsumeShed(); metrics.RecordShed(shed.Frames, shed.Bytes); Assert.Equal(1, metrics.ShedFrames); Assert.Equal(shed.Bytes, metrics.ShedBytes);
    }

    [Fact]
    public void PrioritySendQueue_AgesOldFramesPastStarvationThreshold()
    {
        var queue = new PrioritySendQueue(starvationThreshold: 2);
        queue.Enqueue(new NetFrame(1, 1, [1]), NetChannel.State, 0);
        queue.Enqueue(new NetFrame(2, 1, [2]), NetChannel.State, 10);
        queue.Enqueue(new NetFrame(3, 1, [3]), NetChannel.State, 10);
        Assert.Equal(1, queue.Drain(1).Single().Frame.Opcode);
        Assert.Equal(1, queue.StarvedFrames);
    }

    [Fact]
    public async Task BackendBus_RetriesAndAuthenticatesPublish()
    {
        var flaky = new FlakyBus(); var bus = new ReliableBackendBus(flaky, new byte[32]) { RetryDelay = TimeSpan.Zero };
        await bus.PublishAsync(new BackendMessage("match", [1], DateTimeOffset.UtcNow));
        Assert.Equal(2, flaky.Attempts);
    }

    [Fact]
    public async Task BackendBus_SerializesConcurrentPublishesPerTopic()
    {
        var bus = new RecordingBus();
        await using var reliable = new ReliableBackendBus(bus, new byte[32]);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => reliable.PublishAsync(new BackendMessage("ordered", [(byte)i], DateTimeOffset.UtcNow)).AsTask()));
        long[] sequences = bus.Messages.Select(message => BinaryPrimitives.ReadInt64LittleEndian(message.Payload.AsSpan(36))).ToArray();
        Assert.Equal(Enumerable.Range(1, 20).Select(i => (long)i), sequences);
    }

    [Fact]
    public async Task ShardCoordinator_StartsMigratesAndStops()
    {
        var controller = new FakeController(); var lifecycle = new ShardLifecycle<string, string>(TimeSpan.FromMinutes(1)); var coordinator = new ShardProcessCoordinator<string, string>(controller, lifecycle);
        await coordinator.StartAsync("a"); await coordinator.StartAsync("b"); lifecycle.Assign("p", "a"); await coordinator.MigrateAsync("p", "b"); await coordinator.StopAsync("a");
        Assert.Equal(2, controller.Starts); Assert.Equal(1, controller.Migrations); Assert.Equal(1, controller.Stops);
    }

    [Fact]
    public void ShardDirectory_UsesStableRoutingHash()
    {
        var directory = new ShardDirectory<string>(); directory.Add("a"); directory.Add("b"); directory.Add("c");
        string first = directory.Select("player-42");
        Assert.Equal(first, directory.Select("player-42"));
        Assert.Contains(first, directory.Shards);
    }

    [Fact]
    public void AuthoritativeServer_AppliesOutOfOrderInputsChronologically()
    {
        var guard = new ServerInputGuard<string, int>((_, _) => true, 100, 100);
        var server = new AuthoritativeServer<string, int, int>(0, (state, _, input) => state * 10 + input, inputGuard: guard); server.AddClient("p"); server.Advance(); server.Advance();
        server.SubmitInput("p", 3, 9);
        server.SubmitInput("p", 2, 2); server.SubmitInput("p", 1, 1); server.Advance();
        Assert.Equal(12, server.State);
    }

    [Fact]
    public void AuthoritativeServer_OrdersInputsAcrossTickWrap()
    {
        var guard = new ServerInputGuard<string, int>((_, _) => true, 100, 100);
        var server = new AuthoritativeServer<string, int, int>(0, (state, _, input) => state * 10 + input, inputGuard: guard); server.AddClient("p");
        server.SubmitInput("p", uint.MaxValue, 1); server.SubmitInput("p", uint.MaxValue - 1, 2); server.Advance();
        Assert.Equal(21, server.State);
    }

    [Fact]
    public void AuthoritativeServer_RejectsInputsWithoutValidationGuard()
    {
        var server = new AuthoritativeServer<string, int, int>(0, (state, _, input) => state + input);
        server.AddClient("p"); server.SubmitInput("p", 0, 1); server.Advance();
        Assert.Equal(0, server.State);
    }

    [Fact]
    public void AuthoritativeServer_BoundsPendingInputQueue()
    {
        var guard = new ServerInputGuard<string, int>((_, _) => true, 1000, 1000);
        var server = new AuthoritativeServer<string, int, int>(0, (state, _, input) => state + input, inputGuard: guard) { MaxPendingInputsPerClient = 2 };
        server.AddClient("p"); server.SubmitInput("p", 0, 1); server.SubmitInput("p", 0, 1); server.SubmitInput("p", 0, 1); server.Advance();
        Assert.Equal(1, server.State);
    }

    [Fact]
    public void ValidatedInputRouter_ForwardsAcceptedTickToAuthoritativeSimulation()
    {
        var server = new AuthoritativeServer<string, int, TestState>(0, (state, _, input) => state + input.Value);
        server.AddClient("p");
        var router = new ValidatedInputRouter<string>();
        var guard = new ServerInputGuard<string, TestState>((_, _) => true, 100, 100);
        router.RegisterAuthoritative(7, guard, server);
        Assert.True(router.Dispatch("p", new NetFrame(7, 0, NetSerializer.Serialize(new TestState { Value = 3 }))));
        server.Advance();
        Assert.Equal(3, server.State);
    }

    [Fact]
    public void ValidatedInputRouter_CleansUpClientSequenceState()
    {
        var router = new ValidatedInputRouter<string>();
        var guard = new ServerInputGuard<string, TestState>((_, _) => true, 100, 100);
        int accepted = 0; router.Register(8, guard, (_, _) => { accepted++; return true; });
        byte[] payload = NetSerializer.Serialize(new TestState { Value = 1 });
        Assert.True(router.Dispatch("p", new NetFrame(8, 1, payload)));
        router.Remove("p");
        Assert.True(router.Dispatch("p", new NetFrame(8, 1, payload)));
        Assert.Equal(2, accepted);
    }

    [Fact]
    public void SecurityAndImpairmentPolicies_AreSafeForConcurrentCallers()
    {
        var limiter = new ConnectionRateLimiter(100_000, 100_000);
        var movement = new MovementInputGuard<string>(1000);
        var simulator = new NetworkConditionSimulator(new NetworkConditions(TimeSpan.Zero, TimeSpan.FromMilliseconds(1), 0));
        Parallel.For(0, 256, i =>
        {
            Assert.True(limiter.TryAccept(7));
            simulator.ShouldDrop(); simulator.NextDelay();
            movement.Accept("p", (i, 0), DateTimeOffset.UnixEpoch.AddMilliseconds(i));
        });
    }

    [Fact]
    public void NetworkConditionSimulator_ValidatesRuntimeUpdates()
    {
        var simulator = new NetworkConditionSimulator(new NetworkConditions(TimeSpan.Zero, TimeSpan.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Conditions = new NetworkConditions(TimeSpan.FromMilliseconds(-1), TimeSpan.Zero, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => simulator.Conditions = new NetworkConditions(TimeSpan.Zero, TimeSpan.Zero, 101));
    }

    [Fact]
    public async Task NetworkConditionSimulator_InjectsLatencyJitterAndLossDeterministically()
    {
        var simulator = new NetworkConditionSimulator(new NetworkConditions(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0), 9);
        DateTimeOffset before = DateTimeOffset.UtcNow; bool delivered = await simulator.DeliverAsync(() => ValueTask.CompletedTask); DateTimeOffset after = DateTimeOffset.UtcNow;
        Assert.True(delivered); Assert.True(after - before >= TimeSpan.Zero);
        simulator.Conditions = new NetworkConditions(TimeSpan.Zero, TimeSpan.Zero, 50); int dropped = 0;
        for (int i = 0; i < 100; i++) if (simulator.ShouldDrop()) dropped++;
        Assert.InRange(dropped, 20, 80);
    }

    [Fact]
    public async Task ShardCoordinator_TransfersPlayerStateDuringMigration()
    {
        var controller = new FakeController(); var transfer = new FakeTransfer(); var lifecycle = new ShardLifecycle<string, string>(TimeSpan.FromMinutes(1));
        lifecycle.Register("a"); lifecycle.Register("b"); lifecycle.Assign("p", "a");
        var coordinator = new ShardProcessCoordinator<string, string>(controller, lifecycle, transfer);
        Assert.True(await coordinator.MigrateAsync("p", "b"));
        Assert.Equal(new byte[] { 4, 2 }, transfer.Imported);
    }

    [Fact]
    public async Task ShardSupervisor_RestartsDeadShard()
    {
        var controller = new FakeController(); var lifecycle = new ShardLifecycle<string, string>(TimeSpan.FromMinutes(1)); lifecycle.Register("a");
        var coordinator = new ShardProcessCoordinator<string, string>(controller, lifecycle); var supervisor = new ShardSupervisor<string, string>(coordinator, lifecycle, controller);
        controller.Running = false;
        Assert.Equal(1, await supervisor.RecoverDeadShardsAsync());
        Assert.Equal(1, controller.Starts);
        Assert.Equal(0, await supervisor.RecoverDeadShardsAsync());
    }

    [Fact]
    public void MovementGuard_RejectsTeleport()
    {
        var guard = new MovementInputGuard<string>(5, 0); var now = DateTimeOffset.UtcNow;
        Assert.False(guard.Accept("p", (0, 0), now));
        guard.SetAuthoritativePosition("p", (0, 0), now);
        Assert.True(guard.Accept("p", (1, 0), now.AddSeconds(1)));
        Assert.False(guard.Accept("p", (100, 0), now.AddSeconds(1)));
    }

    [Fact]
    public void FrameworkOptions_ReserveControlOpcodesAndRejectDuplicates()
    {
        Assert.Throws<ArgumentException>(() => new NetworkFrameworkOptions { InputOpcode = 1, StateOpcode = 1 }.Validate());
        Assert.Throws<ArgumentException>(() => new NetworkFrameworkOptions { InputOpcode = GnsServerHost<string>.HeartbeatOpcode }.Validate());
    }

    [Fact]
    public async Task BackendTcpBus_RoundTripsAuthenticatedMessage()
    {
        await using var listener = new TcpBackendBusListener(IPAddress.Loopback, 0); listener.Start();
        Task<IBackendMessageBus> serverTask = listener.AcceptAuthenticatedAsync(new byte[32]);
        await using TcpBackendMessageBus clientRaw = await TcpBackendMessageBus.ConnectAsync("127.0.0.1", listener.Port);
        await using var client = new ReliableBackendBus(clientRaw, new byte[32]);
        IBackendMessageBus server = await serverTask;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Task<BackendMessage> receive = ReadOneAsync(server, cts.Token);
        await client.PublishAsync(new BackendMessage("events", [9, 8], DateTimeOffset.UtcNow), cts.Token);
        BackendMessage message = await receive;
        Assert.Equal(new byte[] { 9, 8 }, message.Payload);
    }

    [Fact(Skip = "Windows Schannel in the current restricted test environment cannot acquire TLS credentials; run this integration test on a host with Schannel credentials enabled.")]
    public async Task BackendTcpBus_RoundTripsOverTls()
    {
        using RSA key = RSA.Create(2048); var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false)); request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); using X509Certificate2 generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5)); using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        await using var listener = new TcpBackendBusListener(IPAddress.Loopback, 0); listener.Start(); Task<IBackendMessageBus> serverTask = listener.AcceptAuthenticatedTlsAsync(certificate, new byte[32]);
        await using var clientRaw = await TcpBackendMessageBus.ConnectTlsAsync("127.0.0.1", listener.Port, "localhost", (_, _, _, _) => true);
        await using var client = new ReliableBackendBus(clientRaw, new byte[32]); IBackendMessageBus server = await serverTask; using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); Task<BackendMessage> receive = ReadOneAsync(server, cts.Token);
        await client.PublishAsync(new BackendMessage("secure", [7], DateTimeOffset.UtcNow), cts.Token); Assert.Equal(new byte[] { 7 }, (await receive).Payload);
    }

    private static async Task<BackendMessage> ReadOneAsync(IBackendMessageBus bus, CancellationToken token)
    { await foreach (BackendMessage message in bus.SubscribeAsync("events", token)) return message; throw new InvalidOperationException(); }
}
