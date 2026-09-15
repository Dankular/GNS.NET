namespace GnsNet.Tests;

using GnsNet;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Xunit;

public sealed class ReplicationPrimitivesTests
{
    [Fact]
    public void Compression_RoundTripsAndEnforcesDecompressionLimit()
    {
        byte[] original = Enumerable.Repeat((byte)7, 10_000).ToArray(); byte[] compressed = SnapshotCompression.Compress(original);
        Assert.Equal(original, SnapshotCompression.Decompress(compressed)); Assert.Throws<InvalidDataException>(() => SnapshotCompression.Decompress(compressed, 10));
    }

    [Fact]
    public void SpatialHash_CullsByRadiusAndVisibility()
    {
        var manager = new SpatialHashInterestManager<int, (int Id, float X, float Y)>(10); var entities = new[] { (1, 2f, 2f), (2, 100f, 100f), (3, 3f, 3f) };
        manager.SetView(4, new(0, 0, 10)); manager.Rebuild(entities, e => (e.X, e.Y));
        Assert.Equal(new[] { 1 }, manager.Cull(4, e => (e.X, e.Y), e => e.Id == 1).Select(e => e.Id));
    }

    [Fact]
    public void HitboxHistory_RewindsAndOrdersRayHits()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(1, 5, 0, 1), new RewindHitbox(2, 8, 0, 1)]);
        IReadOnlyList<RewindHit> hits = history.Raycast(10, 0, 0, 1, 0, 20);
        Assert.Equal(new long[] { 1, 2 }, hits.Select(x => x.EntityId)); Assert.Equal(10u, hits[0].Tick);
    }

    [Fact]
    public void ParallelEncoder_SharesCacheAcrossConcurrentPreparation()
    {
        int encodes = 0; var cache = new SnapshotEncodeCache<int>(2); var encoder = new ParallelSnapshotEncoder<int, int>(cache);
        byte[][] result = encoder.Encode(new[] { 1, 1, 2, 2 }, x => x, _ => { Interlocked.Increment(ref encodes); return [9]; }).ToArray();
        Assert.All(result, bytes => Assert.Equal(new byte[] { 9 }, bytes)); Assert.Equal(2, encodes); Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void SpatialHash_AppliesSceneAndTeamFilters()
    {
        var manager = new SpatialHashInterestManager<int, (int Id, float X, float Y, string Scene, string Team)>(10);
        var entities = new[] { (1, 1f, 1f, "A", "red"), (2, 2f, 2f, "B", "red"), (3, 3f, 3f, "A", "blue") };
        manager.SetView(1, new(0, 0, 10)); manager.Rebuild(entities, e => (e.X, e.Y));
        Assert.Equal(new[] { 1 }, manager.Cull(1, e => (e.X, e.Y), e => e.Scene, e => e.Team, "A", "red").Select(e => e.Id));
    }

    [Fact]
    public void SpatialHash_AppliesOwnerAndOcclusionFilters()
    {
        var manager = new SpatialHashInterestManager<int, (int Id, float X, float Y, string Owner)>(10); var entities = new[] { (1, 1f, 1f, "p"), (2, 2f, 2f, "q") }; manager.SetView(1, new(0, 0, 10)); manager.Rebuild(entities, e => (e.X, e.Y));
        Assert.Equal(new[] { 1 }, manager.Cull(1, e => (e.X, e.Y), _ => "A", e => "red", e => e.Owner, "A", "red", "p", occlusion: e => e.Id == 1).Select(e => e.Id));
    }

    [Fact]
    public async Task TraversalTester_UsesRelayWhenDirectFails()
    {
        P2PTraversalResult result = await P2PTraversalTester.TestAsync(_ => Task.FromResult(false), _ => Task.FromResult(true), TimeSpan.FromSeconds(1));
        Assert.False(result.DirectPath); Assert.True(result.RelayPath); Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task TraversalTester_ContinuesToRelayAfterDirectProbeFailureAndReportsBothFailures()
    {
        P2PTraversalResult fallback = await P2PTraversalTester.TestAsync(
            _ => throw new InvalidOperationException("blocked"), _ => Task.FromResult(true), TimeSpan.FromSeconds(1));
        Assert.False(fallback.DirectPath);
        Assert.True(fallback.RelayPath);
        Assert.Null(fallback.FailureReason);

        P2PTraversalResult failed = await P2PTraversalTester.TestAsync(
            _ => throw new InvalidOperationException("direct blocked"), _ => throw new InvalidOperationException("relay blocked"), TimeSpan.FromSeconds(1));
        Assert.False(failed.RelayPath);
        Assert.Contains("direct blocked", failed.FailureReason);
        Assert.Contains("relay blocked", failed.FailureReason);
    }

    [Fact]
    public void TurnCredentialRotator_ProducesCoturnRestCredentialsWithoutLeakingSecret()
    {
        var rotator = new TurnCredentialRotator([new TurnRelayServer(new Uri("turn:relay.example.test:3478"), "secret")], TimeSpan.FromMinutes(10));
        IReadOnlyList<TurnCredential> credentials = rotator.Issue("player", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        TurnCredential credential = Assert.Single(credentials); Assert.Equal("1700000600:player", credential.Endpoint.Username);
        byte[] expected = HMACSHA1.HashData(Encoding.UTF8.GetBytes("secret"), Encoding.UTF8.GetBytes("1700000600:player"));
        Assert.Equal(Convert.ToBase64String(expected), credential.Endpoint.Credential); Assert.DoesNotContain("secret", credential.Endpoint.Url);
    }

    [Fact]
    public void TurnCredentialRotator_SelectsUnexpiredFallbackRelaysAndRotates()
    {
        var rotator = new TurnCredentialRotator([
            new TurnRelayServer(new Uri("turn:first.example.test:3478"), "a"),
            new TurnRelayServer(new Uri("turn:second.example.test:3478"), "b")]);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000); IReadOnlyList<TurnCredential> first = rotator.Issue("p", now);
        var relays = first.Select(x => x.Endpoint).ToArray(); relays[0] = relays[0] with { ExpiresAt = now.AddSeconds(-1) };
        Assert.Equal("turn:second.example.test:3478", Assert.Single(TurnCredentialRotator.SelectFallback(relays, now)).Url);
        Assert.NotEqual(first[0].Endpoint.Credential, rotator.Issue("p", now.AddMinutes(1))[0].Endpoint.Credential);
        Assert.Throws<ArgumentException>(() => rotator.Issue("bad:user", now));
    }

    [Fact]
    public void HitboxHistory_SupportsSubTickSphereAndBoxQueries()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(1, 5, 0, 1)]); history.Record(11, [new RewindHitbox(1, 7, 0, 1)]);
        Assert.Single(history.RaycastSubTick(10.5, 0, 0, 1, 0, 20)); Assert.Single(history.SphereCast(11, 7, 0, .1f)); Assert.Single(history.BoxCast(11, 6, -1, 8, 1));
    }

    [Fact]
    public void HitboxHistory_SubTickUsesBracketingFramesWhenTicksAreMissing()
    {
        var history = new HitboxRewindHistory();
        history.Record(10, [new RewindHitbox(1, 5, 0, 1)]);
        history.Record(20, [new RewindHitbox(1, 15, 0, 1)]);

        IReadOnlyList<RewindHit> hits = history.RaycastSubTick(15, 0, 0, 1, 0, 20);

        RewindHit hit = Assert.Single(hits);
        Assert.Equal(1, hit.EntityId);
        Assert.Equal(15u, hit.Tick);
        Assert.InRange(hit.Distance, 8.99f, 9.01f);
    }

    [Fact]
    public void HitboxHistory_EnforcesPerFrameBudgetAndCountsRejections()
    {
        var history = new HitboxRewindHistory(maxHitboxesPerFrame: 2);
        history.Record(1, new[] { new RewindHitbox(1, 0, 0, 1), new RewindHitbox(2, 2, 0, 1), new RewindHitbox(3, 4, 0, 1) });
        Assert.Equal(1, history.RejectedHitboxes); Assert.Equal(2, history.Raycast(1, 0, 0, 1, 0, 10).Count);
    }

    [Fact]
    public void HitboxHistory_EvictsOldestFramesAtTotalRetentionBudget()
    {
        var history = new HitboxRewindHistory(capacity: 8, maxRetainedHitboxes: 3);
        history.Record(1, [new RewindHitbox(1, 2, 0, 1), new RewindHitbox(2, 4, 0, 1)]);
        history.Record(2, [new RewindHitbox(3, 6, 0, 1), new RewindHitbox(4, 8, 0, 1)]);

        Assert.Equal(1, history.Count);
        Assert.Equal(2, history.RetainedHitboxes);
        Assert.Equal(3, history.MaxRetainedHitboxes);
        Assert.Equal(0, history.RejectedHitboxes);
        Assert.Equal(new long[] { 3, 4 }, history.Raycast(1, 0, 0, 1, 0, 20).Select(hit => hit.EntityId));
        Assert.Equal(new long[] { 3, 4 }, history.Raycast(2, 0, 0, 1, 0, 20).Select(hit => hit.EntityId));
    }

    [Fact]
    public void HitboxHistory_RejectsInvalidAndDuplicateRegistrations()
    {
        var history = new HitboxRewindHistory();
        history.Record(1, [new RewindHitbox(0, 0, 0, 1), new RewindHitbox(1, float.NaN, 0, 1), new RewindHitbox(2, 0, 0, -1), new RewindHitbox(3, 0, 0, float.PositiveInfinity), new RewindHitbox(4, 2, 0, 1), new RewindHitbox(4, 3, 0, 1)]);

        Assert.Equal(4, history.RejectedInvalidHitboxes);
        Assert.Equal(1, history.RejectedDuplicateHitboxes);
        Assert.Equal(1, history.RetainedHitboxes);
        Assert.Single(history.Raycast(1, 0, 0, 1, 0, 20));
    }

    [Fact]
    public void RewindViewTimeRegistry_RejectsUnregisteredAndOutOfWindowClaims()
    {
        var registry = new RewindViewTimeRegistry<string>(); var authorization = new RewindAuthorization(TimeSpan.FromSeconds(1)); DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        Assert.False(registry.TryGetAuthorized("missing", now, authorization, out _));
        registry.Record("player", now.AddSeconds(-2)); Assert.False(registry.TryGetAuthorized("player", now, authorization, out _));
        registry.Record("player", now.AddMilliseconds(-100)); Assert.True(registry.TryGetAuthorized("player", now, authorization, out DateTimeOffset authorized)); Assert.Equal(now.AddMilliseconds(-100), authorized);
        Assert.True(registry.Remove("player"));
    }

    [Fact]
    public void HitboxHistory_RaycastForClientRequiresTrackedViewTime()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(7, 5, 0, 1)]); var viewTimes = new RewindViewTimeRegistry<string>(); var auth = new RewindAuthorization(TimeSpan.FromSeconds(2)); DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        Assert.Empty(history.RaycastForClient("p", now, viewTimes, auth, _ => 10, 0, 0, 1, 0, 20));
        viewTimes.Record("p", now.AddSeconds(-1)); Assert.Single(history.RaycastForClient("p", now, viewTimes, auth, _ => 10, 0, 0, 1, 0, 20));
    }

    [Fact]
    public void HitboxHistory_RaycastSubTickForClientAuthorizesAndFiltersTargets()
    {
        var history = new HitboxRewindHistory();
        history.Record(10, [new RewindHitbox(7, 5, 0, 1), new RewindHitbox(8, 6, 0, 1)]);
        history.Record(11, [new RewindHitbox(7, 7, 0, 1), new RewindHitbox(8, 8, 0, 1)]);
        var viewTimes = new RewindViewTimeRegistry<string>();
        var authorization = new RewindAuthorization(TimeSpan.FromSeconds(2));
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);

        Assert.Empty(history.RaycastSubTickForClient("shooter", now, viewTimes, authorization, _ => 10.5, id => id == 7, 0, 0, 1, 0, 20));
        viewTimes.Record("shooter", now.AddSeconds(-1));
        IReadOnlyList<RewindHit> hits = history.RaycastSubTickForClient("shooter", now, viewTimes, authorization, _ => 10.5, id => id == 7, 0, 0, 1, 0, 20);
        RewindHit hit = Assert.Single(hits);
        Assert.Equal(7, hit.EntityId);
        Assert.Equal((uint)10, hit.Tick);
    }

    [Fact]
    public void HitboxHistory_RaycastSubTickForClientRejectsExpiredAndFutureViewTimes()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(7, 5, 0, 1)]);
        var viewTimes = new RewindViewTimeRegistry<string>(); var authorization = new RewindAuthorization(TimeSpan.FromSeconds(2)); DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        viewTimes.Record("expired", now.AddSeconds(-3)); viewTimes.Record("future", now.AddSeconds(1));
        Assert.Empty(history.RaycastSubTickForClient("expired", now, viewTimes, authorization, _ => 10, null, 0, 0, 1, 0, 20));
        Assert.Empty(history.RaycastSubTickForClient("future", now, viewTimes, authorization, _ => 10, null, 0, 0, 1, 0, 20));
    }

    [Fact]
    public void AuthoritativeRewindService_ComposesMeasuredViewTimeAndTargetAuthorization()
    {
        var service = new AuthoritativeRewindService<string>(
            time => 10 + (time - DateTimeOffset.UnixEpoch.AddSeconds(10)).TotalSeconds * 10,
            TimeSpan.FromSeconds(2));
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        service.RecordFrame(10, [new RewindHitbox(7, 5, 0, 1), new RewindHitbox(8, 6, 0, 1)]);
        service.RecordFrame(11, [new RewindHitbox(7, 7, 0, 1), new RewindHitbox(8, 8, 0, 1)]);
        service.RecordViewTime("shooter", now.AddMilliseconds(-50));

        IReadOnlyList<RewindHit> hits = service.Raycast("shooter", now, id => id == 7, 0, 0, 1, 0, 20);

        RewindHit hit = Assert.Single(hits);
        Assert.Equal(7, hit.EntityId);
        Assert.Equal((uint)10, hit.Tick);
    }

    [Fact]
    public void AuthoritativeRewindService_RemovesClientAndRejectsStaleViewTime()
    {
        var service = new AuthoritativeRewindService<string>(_ => 10, TimeSpan.FromSeconds(1));
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddSeconds(10);
        service.RecordFrame(10, [new RewindHitbox(7, 5, 0, 1)]);
        service.RecordViewTime("shooter", now.AddMilliseconds(-500));

        Assert.Single(service.Raycast("shooter", now, null, 0, 0, 1, 0, 20));
        Assert.True(service.RemoveClient("shooter"));
        Assert.Empty(service.Raycast("shooter", now, null, 0, 0, 1, 0, 20));
        Assert.False(service.RemoveClient("shooter"));
    }

    [Fact]
    public void ObserverTracker_EmitsOnlyEnterAndLeaveTransitions()
    {
        var tracker = new ObserverTracker<int, int>(); var entered = new List<int>(); var left = new List<int>(); tracker.Entered += (_, entity) => entered.Add(entity); tracker.Left += (_, entity) => left.Add(entity);
        tracker.Update(1, [2, 3]); tracker.Update(1, [3, 4]); Assert.Equal(new[] { 2, 3, 4 }, entered); Assert.Equal(new[] { 2 }, left);
    }

    [Fact]
    public void LifecycleScheduler_QueuesOnlyVisibleAuthoritativeChanges()
    {
        var registry = new NetworkObjectRegistry<string>();
        var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind]);
        scheduler.AddClient("a"); scheduler.UpdateVisibility("a", [1], 4);
        NetworkObjectDescriptor entity = registry.Spawn(2, "a", 4);
        scheduler.UpdateVisibility("a", [entity.ObjectId], 5);
        var frames = scheduler.Drain("a", 10);
        Assert.Single(frames); Assert.Equal((byte)NetworkObjectChangeKind.Spawned, frames[0].Frame.Payload[0]);
        Assert.True(registry.Despawn(entity.ObjectId, "gone"));
        Assert.Single(scheduler.Drain("a", 10));
    }

    [Fact]
    public void LifecycleScheduler_EmitsSpawnWhenExistingObjectEntersAoi()
    {
        var registry = new NetworkObjectRegistry<string>(); var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind]); scheduler.AddClient("a");
        NetworkObjectDescriptor entity = registry.Spawn(2, "a", 1); Assert.Empty(scheduler.Drain("a", 10));
        scheduler.UpdateVisibility("a", [entity.ObjectId], 2); var frame = Assert.Single(scheduler.Drain("a", 10)); Assert.Equal((byte)NetworkObjectChangeKind.Spawned, frame.Frame.Payload[0]);
        scheduler.UpdateVisibility("a", [], 3); var despawn = Assert.Single(scheduler.Drain("a", 10)); Assert.Equal((byte)NetworkObjectChangeKind.Despawned, despawn.Frame.Payload[0]);
    }

    [Fact]
    public void LifecycleScheduler_AutomaticAoiRefreshDrivesSpawnAndDespawnDelivery()
    {
        var registry = new NetworkObjectRegistry<string>();
        var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind, (byte)change.Object.ObjectId]);
        scheduler.AddClient("a");
        var visible = new HashSet<long>();
        scheduler.ConfigureAutomaticVisibility(_ => visible);

        NetworkObjectDescriptor hidden = registry.Spawn(2, "a", 1);
        scheduler.Tick(2);
        Assert.Empty(scheduler.Drain("a", 10));

        visible.Add(hidden.ObjectId);
        scheduler.Tick(3);
        var spawn = Assert.Single(scheduler.Drain("a", 10));
        Assert.Equal((byte)NetworkObjectChangeKind.Spawned, spawn.Frame.Payload[0]);
        Assert.Equal((byte)hidden.ObjectId, spawn.Frame.Payload[1]);

        visible.Clear();
        scheduler.Tick(4);
        var despawn = Assert.Single(scheduler.Drain("a", 10));
        Assert.Equal((byte)NetworkObjectChangeKind.Despawned, despawn.Frame.Payload[0]);
        Assert.Equal((byte)hidden.ObjectId, despawn.Frame.Payload[1]);

        NetworkObjectDescriptor stillHidden = registry.Spawn(3, "a", 5);
        Assert.NotEqual(hidden.ObjectId, stillHidden.ObjectId);
        scheduler.Tick(6);
        Assert.Empty(scheduler.Drain("a", 10));
    }

    [Fact]
    public void LifecycleScheduler_DespawnCleansStaleAoiMembershipAndDoesNotEmitDuplicateLeave()
    {
        var registry = new NetworkObjectRegistry<string>();
        var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind, (byte)change.Object.ObjectId]);
        scheduler.AddClient("a");
        var visible = new HashSet<long>();
        scheduler.ConfigureAutomaticVisibility(_ => visible);
        NetworkObjectDescriptor entity = registry.Spawn(1, "a", 1);
        visible.Add(entity.ObjectId);
        scheduler.Tick(2);
        Assert.Single(scheduler.Drain("a", 10));

        Assert.True(registry.Despawn(entity.ObjectId, "destroyed"));
        var despawn = Assert.Single(scheduler.Drain("a", 10));
        Assert.Equal((byte)NetworkObjectChangeKind.Despawned, despawn.Frame.Payload[0]);

        scheduler.Tick(3);
        Assert.Empty(scheduler.Drain("a", 10));
    }

    [Fact]
    public void LifecycleScheduler_IntegratesSpatialSceneTeamOwnerAndVisibilityFilters()
    {
        var registry = new NetworkObjectRegistry<string>();
        var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind, (byte)change.Object.ObjectId]);
        scheduler.AddClient("alice");
        var interest = new SpatialHashInterestManager<string, AoiEntity>(); interest.SetView("alice", new(0, 0, 20));
        NetworkObjectDescriptor allowed = registry.Spawn(1, "alice", 1);
        NetworkObjectDescriptor wrongTeam = registry.Spawn(1, "alice", 1);
        NetworkObjectDescriptor hidden = registry.Spawn(1, "alice", 1);
        var world = new List<AoiEntity>
        {
            new(allowed.ObjectId, 0, 0, "arena", "red", "alice", true),
            new(wrongTeam.ObjectId, 1, 0, "arena", "blue", "alice", true),
            new(hidden.ObjectId, 2, 0, "arena", "red", "alice", false)
        };
        scheduler.ConfigureAutomaticVisibility(interest, () => world, x => x.ObjectId, x => (x.X, x.Y), x => x.Scene, x => x.Team, x => x.Owner,
            _ => "arena", _ => "red", _ => "alice", x => x.Visible);

        scheduler.Tick(2);
        var spawn = Assert.Single(scheduler.Drain("alice", 10));
        Assert.Equal((byte)NetworkObjectChangeKind.Spawned, spawn.Frame.Payload[0]);
        Assert.Equal((byte)allowed.ObjectId, spawn.Frame.Payload[1]);

        world[0] = world[0] with { Visible = false };
        scheduler.Tick(3);
        var despawn = Assert.Single(scheduler.Drain("alice", 10));
        Assert.Equal((byte)NetworkObjectChangeKind.Despawned, despawn.Frame.Payload[0]);
        Assert.Equal((byte)allowed.ObjectId, despawn.Frame.Payload[1]);
    }

    [Fact]
    public void LifecycleScheduler_RefreshesIndependentClientSceneTeamAndOwnerViews()
    {
        var registry = new NetworkObjectRegistry<string>();
        var scheduler = new LifecycleReplicationScheduler<string>(registry, 90, change => [(byte)change.Kind, (byte)change.Object.ObjectId]);
        scheduler.AddClient("red"); scheduler.AddClient("blue");
        var interest = new SpatialHashInterestManager<string, AoiEntity>();
        interest.SetView("red", new(0, 0, 20)); interest.SetView("blue", new(0, 0, 20));
        NetworkObjectDescriptor redEntity = registry.Spawn(1, "red", 1);
        NetworkObjectDescriptor blueEntity = registry.Spawn(1, "blue", 1);
        NetworkObjectDescriptor redOwnerEntity = registry.Spawn(1, "red", 1);
        var world = new List<AoiEntity>
        {
            new(redEntity.ObjectId, 0, 0, "arena", "red", "red", true),
            new(blueEntity.ObjectId, 1, 0, "arena", "blue", "blue", true),
            new(redOwnerEntity.ObjectId, 2, 0, "arena", "red", "red", true)
        };
        var clientTeams = new Dictionary<string, string> { ["red"] = "red", ["blue"] = "blue" };
        scheduler.ConfigureAutomaticVisibility(interest, () => world, x => x.ObjectId, x => (x.X, x.Y), x => x.Scene, x => x.Team, x => x.Owner,
            _ => "arena", client => clientTeams[client], _ => null, x => x.Visible);

        scheduler.Tick(1);
        Assert.Equal(new[] { redEntity.ObjectId, redOwnerEntity.ObjectId }, scheduler.Drain("red", 10).Select(x => (long)x.Frame.Payload[1]));
        Assert.Equal(new[] { blueEntity.ObjectId }, scheduler.Drain("blue", 10).Select(x => (long)x.Frame.Payload[1]));

        clientTeams["red"] = "blue";
        scheduler.Tick(2);
        IReadOnlyList<(NetFrame Frame, NetChannel Channel)> redTransition = scheduler.Drain("red", 10);
        Assert.Equal(3, redTransition.Count);
        Assert.Equal(new[] { (byte)NetworkObjectChangeKind.Spawned, (byte)NetworkObjectChangeKind.Despawned, (byte)NetworkObjectChangeKind.Despawned }, redTransition.Select(x => x.Frame.Payload[0]));
        Assert.Equal(blueEntity.ObjectId, (long)redTransition[0].Frame.Payload[1]);
    }

    private readonly record struct AoiEntity(long ObjectId, float X, float Y, string Scene, string Team, string Owner, bool Visible);

    [Fact]
    public void RewindAuthorization_ClampsOnlyWithinServerWindowAndRejectsFuture()
    {
        var auth = new RewindAuthorization(TimeSpan.FromSeconds(1)); DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.True(auth.TryGetAuthorizedTime(now, now.AddMilliseconds(-500), out _)); Assert.False(auth.TryGetAuthorizedTime(now, now.AddSeconds(-2), out DateTimeOffset clamped)); Assert.Equal(now.AddSeconds(-1), clamped); Assert.False(auth.TryGetAuthorizedTime(now, now.AddMilliseconds(1), out _));
    }

    [Fact]
    public void HitboxHistory_RejectsUnauthorizedClientViewTime()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(1, 5, 0, 1)]); var auth = new RewindAuthorization(TimeSpan.FromSeconds(1)); DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Empty(history.RaycastAuthorized(now, now.AddSeconds(-2), auth, _ => 10, 0, 0, 1, 0, 20)); Assert.Single(history.RaycastAuthorized(now, now.AddMilliseconds(-500), auth, _ => 10, 0, 0, 1, 0, 20));
    }

    [Fact]
    public async Task SignalingClient_SendsBearerAndReadsRelayPlan()
    {
        var handler = new SignalingHandler(); using var http = new HttpClient(handler); var client = new HttpP2PSignalingClient(http, new Uri("https://signal.test/"), () => "short-lived");
        P2PTraversalPlan result = await client.PublishAsync(new P2PSignal("s", "p", "offer", "payload", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal("Bearer", handler.Authorization?.Scheme); Assert.Equal("short-lived", handler.Authorization?.Parameter); Assert.Equal("s", result.Signal.SessionId);
    }

    private sealed class SignalingHandler : HttpMessageHandler
    {
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { this.Authorization = request.Headers.Authorization; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new P2PTraversalPlan(new P2PSignal("s", "p", "answer", "reply", DateTimeOffset.UtcNow.AddMinutes(1)), [])) }); }
    }
}
