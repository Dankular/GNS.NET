namespace GnsNet.Tests;

using GnsNet;
using System.Net;
using System.Net.Http.Json;
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
    public async Task TraversalTester_UsesRelayWhenDirectFails()
    {
        P2PTraversalResult result = await P2PTraversalTester.TestAsync(_ => Task.FromResult(false), _ => Task.FromResult(true), TimeSpan.FromSeconds(1));
        Assert.False(result.DirectPath); Assert.True(result.RelayPath); Assert.Null(result.FailureReason);
    }

    [Fact]
    public void HitboxHistory_SupportsSubTickSphereAndBoxQueries()
    {
        var history = new HitboxRewindHistory(); history.Record(10, [new RewindHitbox(1, 5, 0, 1)]); history.Record(11, [new RewindHitbox(1, 7, 0, 1)]);
        Assert.Single(history.RaycastSubTick(10.5, 0, 0, 1, 0, 20)); Assert.Single(history.SphereCast(11, 7, 0, .1f)); Assert.Single(history.BoxCast(11, 6, -1, 8, 1));
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
