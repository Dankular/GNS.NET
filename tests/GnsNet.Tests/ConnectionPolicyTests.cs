namespace GnsNet.Tests;

using GnsNet;
using Xunit;
using GnsSharp;
using System.Collections.Generic;

public sealed class ConnectionPolicyTests
{
    private sealed class ConfigurationSink : IGnsNativeConfigurationSink
    {
        public readonly Dictionary<ESteamNetworkingConfigValue, int> Integers = new();
        public readonly Dictionary<ESteamNetworkingConfigValue, string> Strings = new();
        public void SetInt32(ESteamNetworkingConfigValue key, int value) => Integers[key] = value;
        public void SetString(ESteamNetworkingConfigValue key, string value) => Strings[key] = value;
    }

    [Fact]
    public void NativeConfiguration_MapsImpairmentAndP2PSettings()
    {
        var sink = new ConfigurationSink();
        var options = new GnsRuntimeOptions
        {
            Impairment = new GnsImpairmentOptions { LossSendPercent = 4, LagReceiveMilliseconds = 17 },
            P2P = new GnsP2POptions
            {
                IceCandidatePolicy = 1,
                StunServerList = "stun.example.test:3478",
                TurnRelays = [new P2PRelayEndpoint("turn:relay.example.test:3478", "expiry:player", "base64-credential", DateTimeOffset.UtcNow.AddMinutes(5))]
            }
        };
        GnsNativeConfiguration.Apply(options, sink);
        Assert.Equal(4, sink.Integers[ESteamNetworkingConfigValue.FakePacketLoss_Send]);
        Assert.Equal(17, sink.Integers[ESteamNetworkingConfigValue.FakePacketLag_Recv]);
        Assert.Equal(1, sink.Integers[ESteamNetworkingConfigValue.P2P_Transport_ICE_Enable]);
        Assert.Equal("stun.example.test:3478", sink.Strings[ESteamNetworkingConfigValue.P2P_STUN_ServerList]);
        Assert.Equal("turn:relay.example.test:3478", sink.Strings[ESteamNetworkingConfigValue.P2P_TURN_ServerList]);
        Assert.Equal("expiry:player", sink.Strings[ESteamNetworkingConfigValue.P2P_TURN_UserList]);
        Assert.Equal("base64-credential", sink.Strings[ESteamNetworkingConfigValue.P2P_TURN_PassList]);
    }
    [Fact]
    public void P2POptions_RejectExpiredMalformedOrCommaInjectedTurnCredentials()
    {
        static P2PRelayEndpoint Relay(string url, string? user, string? credential, DateTimeOffset expiry) => new(url, user, credential, expiry);
        Assert.Throws<ArgumentException>(() => new GnsP2POptions { TurnRelays = [Relay("udp:relay.example.test", "u", "p", DateTimeOffset.UtcNow.AddMinutes(1))] }.Validate());
        Assert.Throws<ArgumentException>(() => new GnsP2POptions { TurnRelays = [Relay("turn:relay.example.test", "u", "p", DateTimeOffset.UtcNow.AddMinutes(-1))] }.Validate());
        Assert.Throws<ArgumentException>(() => new GnsP2POptions { TurnRelays = [Relay("turn:relay.example.test", "u", "p,unsafe", DateTimeOffset.UtcNow.AddMinutes(1))] }.Validate());
        Assert.Throws<ArgumentException>(() => new GnsP2POptions { TurnRelays = [Relay("turn:relay.example.test", null, "p", DateTimeOffset.UtcNow.AddMinutes(1))] }.Validate());
    }
    [Fact]
    public void TransportPolicy_RejectsUnauthenticatedOrUnencryptedConnections()
    {
        var policy = new TransportSecurityPolicy();
        var unauthenticated = new SteamNetConnectionInfo_t { Flags = ESteamNetworkConnectionInfoFlags.Unauthenticated };
        var unencrypted = new SteamNetConnectionInfo_t { Flags = ESteamNetworkConnectionInfoFlags.Unencrypted };
        Assert.False(policy.Accept(unauthenticated, out _));
        Assert.False(policy.Accept(unencrypted, out _));
    }
    [Fact]
    public void NativeAuthentication_ReportsBackendTicketCapability()
    {
        Assert.True(NativeAuthentication.Capabilities.CertificateTransportValidation);
        Assert.True(NativeAuthentication.Capabilities.CertificateProvisioning);
        Assert.False(NativeAuthentication.Capabilities.SteamAuthTickets);
        Assert.Contains("Steam BeginAuthSession", NativeAuthentication.Capabilities.Limitation);
    }

    [Fact]
    public void NativeAuthentication_RejectsEmptyCertificateWithoutCallingNativeApi()
    {
        Assert.Throws<ArgumentException>(() => NativeAuthentication.SetCertificate(null!, []));
    }

    [Fact]
    public void NativeAuthenticationBinding_ExposesCertificateAndStatusOperations()
    {
        Assert.NotNull(typeof(ISteamNetworkingSockets).GetMethod(nameof(ISteamNetworkingSockets.SetCertificate)));
        Assert.NotNull(typeof(ISteamNetworkingSockets).GetMethod(nameof(ISteamNetworkingSockets.GetCertificateRequest)));
        Assert.NotNull(typeof(ISteamNetworkingSockets).GetMethod(nameof(ISteamNetworkingSockets.GetAuthenticationStatus), new[] { typeof(SteamNetAuthenticationStatus_t).MakeByRefType() }));
    }

    [Fact]
    public void NativeConnectionStatistics_ProjectsBindingQualityIntoPacketLoss()
    {
        var statistics = new NativeConnectionStatistics(default, 20, .75f, .9f, 0, 0, 0, 0, 0, 0, 0, 0, default, null);
        Assert.Equal(25d, statistics.LocalPacketLossPercent, 5);
        Assert.Equal(10d, statistics.RemotePacketLossPercent, 5);

        var invalid = statistics with { LocalQuality = float.NaN, RemoteQuality = float.PositiveInfinity };
        Assert.Equal(0d, invalid.LocalPacketLossPercent);
        Assert.Equal(0d, invalid.RemotePacketLossPercent);
    }

    [Fact]
    public void P2PArguments_RejectInvalidValuesBeforeNativeCall()
    {
        Assert.Throws<ArgumentException>(() => GnsClient.ConnectP2P(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnsClient.ConnectP2P("localhost", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => GnsServer.ListenP2P(-1));
    }

    [Fact]
    public void Impairment_ValidatesBothDirectionsAndJitterBounds()
    {
        new GnsImpairmentOptions { LossSendPercent = 10, LossReceivePercent = 20, JitterSendAverageMilliseconds = 2, JitterSendMaximumMilliseconds = 5, JitterReceiveAverageMilliseconds = 3, JitterReceiveMaximumMilliseconds = 7 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GnsImpairmentOptions { JitterReceiveAverageMilliseconds = 8, JitterReceiveMaximumMilliseconds = 2 }.Validate());
    }

    [Fact]
    public void P2POptions_ValidateIceAndStunConfiguration()
    {
        new GnsP2POptions { IceCandidatePolicy = 1, StunServerList = "stun.example.test:3478" }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new GnsP2POptions { IceCandidatePolicy = -1 }.Validate());
        Assert.Throws<ArgumentException>(() => new GnsP2POptions { StunServerList = new string('x', 4097) }.Validate());
    }

    [Fact]
    public void ControlTraffic_IsNotSubjectToGameplayFloodBucket()
    {
        Assert.True(GnsServerHost<string>.IsControlOpcode(GnsServerHost<string>.HeartbeatOpcode));
        Assert.True(GnsServerHost<string>.IsControlOpcode(GnsServerHost<string>.GracefulDisconnectOpcode));
        Assert.False(GnsServerHost<string>.IsControlOpcode(42));
    }

    [Fact]
    public void ServerHost_RequiresApplicationAdmissionByDefault()
    {
        Assert.True(GnsServerHost<string>.DefaultRequireApplicationAdmission);
    }

    [Fact]
    public async Task TokenBucket_EnforcesBurstAtomicallyAcrossConcurrentConsumers()
    {
        var limiter = new TokenBucketRateLimiter(1, 10); int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => { if (limiter.TryConsume()) Interlocked.Increment(ref accepted); })));
        Assert.Equal(10, accepted);
    }

    [Fact]
    public void InputGuard_FailsClosedWhenValidatorThrows()
    {
        var guard = new ServerInputGuard<string, int>((_, _) => throw new InvalidOperationException(), 10, 2);
        Assert.False(guard.TryAccept("p", 1));
    }

    [Fact]
    public async Task ReconnectableClient_GracefulDisconnectDisablesReconnectLoop()
    {
        var reconnect = new ReconnectableClient(() => throw new InvalidOperationException());
        Assert.True(reconnect.ReconnectEnabled);
        reconnect.DisconnectGracefully();
        Assert.False(reconnect.ReconnectEnabled);
        await reconnect.DisposeAsync();
    }
    [Fact]
    public void Token_RejectsTamperingAndExpiry()
    {
        var service = new ConnectTokenService(new byte[32]);
        string token = service.Issue("player-1", TimeSpan.FromMinutes(1), DateTimeOffset.UnixEpoch);
        Assert.True(service.TryValidate(token, out ConnectClaims claims, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.Equal("player-1", claims.SessionId);
        Assert.False(service.TryValidate(token + "x", out _, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        Assert.False(service.TryValidate(token, out _, DateTimeOffset.UnixEpoch.AddMinutes(2)));
        Assert.Throws<ArgumentException>(() => service.Issue("bad|session", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Admission_ReleasesNonceForSessionResumption()
    {
        var admission = new ConnectionAdmission(new ConnectTokenService(new byte[32]));
        string token = new ConnectTokenService(new byte[32]).Issue("player", TimeSpan.FromMinutes(1));
        Assert.True(admission.TryAdmit(token, out ConnectClaims claims));
        Assert.False(admission.TryAdmit(token, out _));
        Assert.True(admission.Release(claims.Nonce));
        Assert.True(admission.TryAdmit(token, out _));
    }

    [Fact]
    public void SessionRegistry_DistinguishesFirstAttachFromResumption()
    {
        var registry = new ServerSessionRegistry<string>(TimeSpan.FromMinutes(1));
        var connection = new GnsConnection(default);
        Assert.False(registry.Attach("player", connection, DateTimeOffset.UnixEpoch));
        registry.Detach("player", DateTimeOffset.UnixEpoch);
        Assert.True(registry.Attach("player", connection, DateTimeOffset.UnixEpoch.AddSeconds(1)));
    }

    [Fact]
    public void SessionRegistry_ResolvesOnlyTheCurrentlyAttachedTarget()
    {
        var registry = new ServerSessionRegistry<string>(TimeSpan.FromMinutes(1)); var connection = new GnsConnection(default);
        registry.Attach("target", connection);
        Assert.True(registry.TryGet("target", out GnsConnection? resolved)); Assert.Same(connection, resolved);
        registry.Detach("target"); Assert.False(registry.TryGet("target", out _));
    }

    [Fact]
    public void SessionRehydration_ReplaysOrderedLifecycleIdempotentlyAndClearsOnGracefulRemoval()
    {
        var sessions = new ServerSessionRegistry<string>(TimeSpan.FromMinutes(1)); var server = new NetworkObjectRegistry<string>(); var client = new NetworkObjectRegistry<string>();
        NetworkObjectDescriptor entity = server.Spawn(3, "player", 10); sessions.Rehydration.Record("s", new(NetworkObjectChangeKind.Spawned, entity));
        Assert.Equal(1, sessions.Rehydration.Replay("s", client)); Assert.Equal(1, sessions.Rehydration.Replay("s", client)); Assert.Single(client.Objects);
        sessions.Rehydration.Clear("s"); Assert.Empty(sessions.Rehydration.Snapshot("s"));
    }

    [Fact]
    public void M1ExitCriterion_AuthenticatedReconnectRebuildsOwnedObjectGraphAndDispatchesRpc()
    {
        var tokenService = new ConnectTokenService(new byte[32]);
        var admission = new ConnectionAdmission(tokenService);
        string token = tokenService.Issue("player", TimeSpan.FromMinutes(1));
        Assert.True(admission.TryAdmit(token, out ConnectClaims claims));

        var sessions = new ServerSessionRegistry<string>(TimeSpan.FromMinutes(1));
        var firstConnection = new GnsConnection(default); Assert.False(sessions.Attach(claims.SessionId, firstConnection, DateTimeOffset.UnixEpoch));
        var authoritative = new NetworkObjectRegistry<string>(); NetworkObjectDescriptor spawned = authoritative.Spawn(7, claims.SessionId, 1);
        sessions.Rehydration.Record(claims.SessionId, new(NetworkObjectChangeKind.Spawned, spawned));
        NetworkObjectDescriptor transferred = spawned with { OwnerId = "server" };
        sessions.Rehydration.Record(claims.SessionId, new(NetworkObjectChangeKind.OwnershipTransferred, transferred, claims.SessionId));

        sessions.Detach(claims.SessionId, DateTimeOffset.UnixEpoch.AddSeconds(1)); Assert.True(admission.Release(claims.Nonce));
        Assert.True(admission.TryAdmit(token, out ConnectClaims resumedClaims));
        Assert.Equal(claims.SessionId, resumedClaims.SessionId);
        Assert.True(sessions.Attach(resumedClaims.SessionId, new GnsConnection(default), DateTimeOffset.UnixEpoch.AddSeconds(2)));

        var rebuilt = new NetworkObjectRegistry<string>();
        foreach (NetworkObjectChange change in sessions.Rehydration.Snapshot(resumedClaims.SessionId)) Assert.True(rebuilt.Apply(change));
        Assert.True(rebuilt.TryGet(spawned.ObjectId, out NetworkObjectDescriptor rebuiltObject)); Assert.Equal("server", rebuiltObject.OwnerId);

        var rpc = new RpcRouter(); rpc.Register("player.ready", RpcAuthority.AnyAuthenticated, request => new(request.RequestId, true, request.Payload));
        RpcResponse response = rpc.Dispatch(new(Guid.NewGuid(), "player.ready", spawned.ObjectId, resumedClaims.SessionId, [1], 2));
        Assert.True(response.Accepted); Assert.Equal(new byte[] { 1 }, response.Payload);
    }

    [Fact]
    public void SessionRehydration_ReplaysReconnectDuringSpawnSequenceInOrder()
    {
        var buffer = new SessionRehydrationBuffer<string>(); var client = new NetworkObjectRegistry<string>();
        var first = new NetworkObjectDescriptor(1, 3, "player", 10); var second = new NetworkObjectDescriptor(2, 4, "player", 11);
        buffer.Record("player", new(NetworkObjectChangeKind.Spawned, first));
        buffer.Record("player", new(NetworkObjectChangeKind.OwnershipTransferred, first with { OwnerId = "server" }, "player"));
        buffer.Record("player", new(NetworkObjectChangeKind.Spawned, second));
        buffer.Record("player", new(NetworkObjectChangeKind.Despawned, first, Reason: "replaced"));

        Assert.Equal(4, buffer.Snapshot("player").Count);
        Assert.Equal(4, buffer.Replay("player", client));
        Assert.False(client.TryGet(first.ObjectId, out _));
        Assert.True(client.TryGet(second.ObjectId, out NetworkObjectDescriptor restored));
        Assert.Equal(second, restored);
        Assert.Equal(4, buffer.Replay("player", client));
    }

    [Fact]
    public void SessionRehydration_ReportsRetentionOverflowForBaselineFallback()
    {
        var buffer = new SessionRehydrationBuffer<string>(2); var entity = new NetworkObjectDescriptor(1, 1, "a", 1);
        buffer.Record("s", new(NetworkObjectChangeKind.Spawned, entity));
        buffer.Record("s", new(NetworkObjectChangeKind.OwnershipTransferred, entity with { OwnerId = "b" }, "a"));
        buffer.Record("s", new(NetworkObjectChangeKind.OwnershipTransferred, entity with { OwnerId = "c" }, "b"));
        Assert.True(buffer.RequiresBaseline("s")); buffer.Clear("s"); Assert.False(buffer.RequiresBaseline("s"));
    }

    [Fact]
    public void SessionGraceTracker_ExposesGraceTrackedSessionsForLifecycleJournaling()
    {
        var tracker = new SessionGraceTracker<string>(TimeSpan.FromMinutes(1)); tracker.MarkDisconnected("s", DateTimeOffset.UtcNow);
        Assert.Contains("s", tracker.Tracked);
    }

    [Fact]
    public void Heartbeat_SeparatesGameTimeoutFromTransport()
    {
        var monitor = new HeartbeatMonitor<string>(TimeSpan.FromSeconds(5));
        monitor.Touch("p", DateTimeOffset.UnixEpoch);
        Assert.Equal(HeartbeatStatus.Alive, monitor.GetStatus("p", DateTimeOffset.UnixEpoch.AddSeconds(5)));
        Assert.Equal(HeartbeatStatus.TimedOut, monitor.GetStatus("p", DateTimeOffset.UnixEpoch.AddSeconds(6)));
    }

    [Fact]
    public async Task HeartbeatMonitor_IsSafeAcrossTouchAndPollThreads()
    {
        var monitor = new HeartbeatMonitor<int>(TimeSpan.FromSeconds(1));
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => { monitor.Touch(i); monitor.GetStatus(i); monitor.Poll(); })));
        Assert.Equal(HeartbeatStatus.Alive, monitor.GetStatus(99));
    }

    [Fact]
    public void HeartbeatTracker_MeasuresAcknowledgementAndRejectsDuplicates()
    {
        var tracker = new HeartbeatTracker(); var metrics = new ConnectionMetrics(); byte[] probe = tracker.CreateProbe(metrics);
        Assert.True(tracker.Acknowledge(probe, metrics)); Assert.False(tracker.Acknowledge(probe, metrics)); Assert.True(metrics.RttMilliseconds >= 0);
        Assert.Equal(1, tracker.SentCount); Assert.Equal(1, tracker.AcknowledgedCount); Assert.Equal(1, tracker.DuplicateAcknowledgementCount);
        Assert.Equal(0, tracker.ExpiredCount); Assert.Equal(0, tracker.LossPercent);
    }

    [Fact]
    public void SequenceLossTracker_ReportsGapsAndDuplicatesWithinBoundedWindow()
    {
        var tracker = new SequenceLossTracker(16);
        uint first = tracker.Next(); uint second = tracker.Next(); uint third = tracker.Next();
        Assert.Equal(1u, first); Assert.Equal(2u, second); Assert.Equal(3u, third);
        Assert.True(tracker.Observe(first)); Assert.True(tracker.Observe(third));
        Assert.False(tracker.Observe(third));
        Assert.Equal(3, tracker.Sent); Assert.Equal(2, tracker.ReceivedUnique); Assert.Equal(1, tracker.Duplicates);
        Assert.Equal(1, tracker.Missing); Assert.Equal(33.333, tracker.LossPercent, 2);
    }

    [Fact]
    public void SequenceLossTracker_BoundsLongRunningObservationWindow()
    {
        var tracker = new SequenceLossTracker(2);
        uint first = tracker.Next(); uint second = tracker.Next(); uint third = tracker.Next();
        Assert.True(tracker.Observe(first)); Assert.True(tracker.Observe(second)); Assert.True(tracker.Observe(third));
        Assert.Equal(3, tracker.Sent); Assert.Equal(1, tracker.ReceivedUnique);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SequenceLossTracker(0));
    }

    [Fact]
    public void SteamAuthSessionState_ConsumesValidationCallbacksAndTracksOwnership()
    {
        var state = new SteamAuthSessionState();
        SteamAuthSessionValidation? received = null;
        state.ValidationReceived += validation => received = validation;
        var response = new ValidateAuthTicketResponse_t
        {
            SteamID = new CSteamID(42),
            AuthSessionResponse = EAuthSessionResponse.OK,
            OwnerSteamID = new CSteamID(99)
        };

        SteamAuthSessionValidation result = state.Apply(in response);
        Assert.True(result.Accepted); Assert.True(result.IsOwnerDifferent); Assert.Equal(result, received);
        Assert.True(state.TryGet(new CSteamID(42), out SteamAuthSessionValidation stored)); Assert.Equal(result, stored);
        Assert.True(state.Remove(new CSteamID(42))); Assert.False(state.TryGet(new CSteamID(42), out _));
    }

    [Fact]
    public async Task HeartbeatTracker_IsSafeAcrossProbeAndAckThreads()
    {
        var tracker = new HeartbeatTracker(); var metrics = new ConnectionMetrics(); var probes = new System.Collections.Concurrent.ConcurrentBag<byte[]>();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => probes.Add(tracker.CreateProbe()))));
        Assert.Equal(100, probes.Count); foreach (byte[] probe in probes) Assert.True(tracker.Acknowledge(probe, metrics));
    }

    [Fact]
    public void Metrics_UsesBoundedProbeLossWindow()
    {
        var metrics = new ConnectionMetrics();
        for (int i = 0; i < 200; i++) { metrics.RecordSequenceAcknowledged(); }
        Assert.Equal(0, metrics.PacketLossPercent);
        for (int i = 0; i < 128; i++) metrics.RecordLostPacket();
        Assert.Equal(100, metrics.PacketLossPercent);
    }

    [Fact]
    public void MetricsOverlay_ExposesConnectionHealth()
    {
        var metrics = new ConnectionMetrics(); metrics.RecordIn(10); metrics.RecordOut(20); metrics.RecordRtt(TimeSpan.FromMilliseconds(12.5));
        ConnectionMetricsSnapshot snapshot = NetworkDebugOverlay.Snapshot("player-1", metrics); string text = NetworkDebugOverlay.Format(snapshot);
        Assert.Contains("player-1", text); Assert.Contains("12.5 ms", text); Assert.Contains("IN 1 pkts/10 B", text); Assert.Contains("OUT 1 pkts/20 B", text);
    }

    [Fact]
    public async Task Recorder_PersistsAndLoadsCapture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gnsnet-{Guid.NewGuid():N}.capture");
        try
        {
            var recorder = new NetworkRecorder();
            recorder.Record(true, [1, 2], DateTimeOffset.UnixEpoch, "session-7", NetChannel.Event);
            await recorder.SaveAsync(path);
            NetworkRecorder loaded = await NetworkRecorder.LoadAsync(path);
            Assert.Single(loaded.Packets);
            Assert.Equal(new byte[] { 1, 2 }, loaded.Packets[0].Data);
            Assert.Equal("session-7", loaded.Packets[0].ConnectionId);
            Assert.Equal(NetChannel.Event, loaded.Packets[0].Channel);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Recorder_RejectsTruncatedCapture()
    {
        string path = Path.Combine(Path.GetTempPath(), $"gnsnet-{Guid.NewGuid():N}.capture");
        try { await File.WriteAllBytesAsync(path, [0x52, 0x53, 0x4E, 0x47, 2]); await Assert.ThrowsAsync<EndOfStreamException>(() => NetworkRecorder.LoadAsync(path)); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Recorder_ReplaysOutboundTransportPacketsInOrder()
    {
        var recorder = new NetworkRecorder();
        recorder.Record(true, [1], DateTimeOffset.UnixEpoch);
        recorder.Record(false, [2], DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        recorder.Record(true, [3], DateTimeOffset.UnixEpoch.AddMilliseconds(2));
        var sent = new List<byte[]>();
        int delivered = await recorder.ReplayTransportAsync(packet => { sent.Add(packet.Data); return ValueTask.FromResult(true); });
        Assert.Equal(2, delivered);
        Assert.Equal(new byte[] { 1 }, sent[0]);
        Assert.Equal(new byte[] { 3 }, sent[1]);
    }

    [Fact]
    public async Task Recorder_PlaybackOrdersCapturedPacketsDeterministically()
    {
        var recorder = new NetworkRecorder();
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        recorder.Record(false, [2], origin.AddSeconds(2));
        recorder.Record(false, [1], origin.AddSeconds(1));

        var packets = new List<byte>();
        await recorder.PlaybackAsync(packet => { packets.Add(packet.Data[0]); return ValueTask.CompletedTask; });

        Assert.Equal(new byte[] { 1, 2 }, packets);
    }

    [Fact]
    public async Task Recorder_IsSafeForConcurrentHostTraffic()
    {
        var recorder = new NetworkRecorder();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() => recorder.Record(i % 2 == 0, [1, 2, 3], connectionId: i.ToString()))));
        Assert.Equal(100, recorder.Packets.Count);
    }

    [Fact]
    public void AdaptiveTickController_HasRecoveryHysteresis()
    {
        var controller = new AdaptiveTickController(TimeSpan.FromMilliseconds(50));
        var policy = new LoadSheddingPolicy { ReduceTickCpuPercent = 80 };
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Update(new ServerLoad(1, 50, 90, 0), policy));
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.Update(new ServerLoad(1, 50, 75, 0), policy));
        Assert.Equal(TimeSpan.FromMilliseconds(50), controller.Update(new ServerLoad(1, 50, 69, 0), policy));
    }

    [Fact]
    public void LoadShedding_RejectsBacklogAndOverlongTicks()
    {
        var policy = new LoadSheddingPolicy { MaximumConnections = 10, MaximumPendingMessages = 5, MaximumTickMilliseconds = 20, RejectCpuPercent = 90 };
        Assert.True(policy.AllowConnection(new ServerLoad(1, 10, 50, 5)));
        Assert.False(policy.AllowConnection(new ServerLoad(1, 10, 50, 6)));
        Assert.False(policy.AllowConnection(new ServerLoad(1, 21, 50, 0)));
        Assert.False(policy.AllowConnection(new ServerLoad(1, 10, 90, 0)));
    }
}
