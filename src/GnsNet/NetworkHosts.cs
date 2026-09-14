namespace GnsNet;

using GnsSharp;
using MemoryPack;
using System.Text;

/// <summary>Application-facing server host: polling, framing, routing, and resumable sessions.</summary>
public sealed class GnsServerHost<TSessionId> where TSessionId : notnull
{
    public const bool DefaultRequireApplicationAdmission = true;
    private readonly GnsServer server;
    private readonly Dictionary<uint, TSessionId> connectionSessions = new();
    private readonly Dictionary<uint, string> connectionNonces = new();
    private readonly ConnectionAdmission? admission;
    private readonly HashSet<uint> authenticated = new();
    public ConnectionRateLimiter RateLimiter { get; } = new();
    public const byte HandshakeOpcode = 0;
    public const byte HeartbeatOpcode = 0xFE;
    public const byte HeartbeatAckOpcode = 0xFC;
    public const byte GracefulDisconnectOpcode = 0xFD;
    private readonly HashSet<uint> gracefullyClosed = new();
    private readonly HashSet<uint> heartbeatTimedOut = new();
    private readonly Dictionary<byte, Action<TSessionId, NetFrame>> snapshotAcknowledgers = new();
    private readonly Dictionary<uint, HeartbeatTracker> heartbeatTrackers = new();
    private readonly Dictionary<uint, DateTimeOffset> heartbeatSentAt = new();
    private NetworkObjectRegistry<TSessionId>? lifecycleRegistry;
    private byte lifecycleOpcode;
    private Func<NetworkObjectChange, byte[]>? lifecycleEncoder;
    public LoadSheddingPolicy? LoadShedding { get; init; }
    public Func<ServerLoad>? LoadProvider { get; init; }
    /// <summary>Requires a signed application admission token before gameplay frames are dispatched.</summary>
    /// <remarks>Set to <see langword="false"/> only for an intentionally open development server.</remarks>
    public bool RequireApplicationAdmission { get; init; } = DefaultRequireApplicationAdmission;
    public NetworkConditionSimulator? Conditions { get; init; }
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(2);
    public NetworkRecorder? Recorder { get; init; }
    public ConnectionMetrics Metrics { get; } = new();
    public System.Collections.Concurrent.ConcurrentDictionary<uint, ConnectionMetrics> MetricsByConnection { get; } = new();
    public GnsServerHost(GnsServer server, TimeSpan sessionGracePeriod, ConnectionAdmission? admission = null, TimeSpan? heartbeatTimeout = null)
    {
        this.server = server ?? throw new ArgumentNullException(nameof(server));
        this.Sessions = new ServerSessionRegistry<TSessionId>(sessionGracePeriod);
        this.admission = admission;
        this.Heartbeats = heartbeatTimeout is null ? null : new HeartbeatMonitor<TSessionId>(heartbeatTimeout.Value);
        if (this.Heartbeats is not null) this.Heartbeats.TimedOut += id => { if (this.Sessions.TryGet(id, out GnsConnection? connection) && connection is not null) { this.heartbeatTimedOut.Add(connection.Handle.Handle); this.server.Reject(connection, "Game heartbeat timeout"); } };
        this.server.ClientDisconnected += (connection, _, _) => this.DetachSession(connection);
    }
    public ServerSessionRegistry<TSessionId> Sessions { get; }
    public HeartbeatMonitor<TSessionId>? Heartbeats { get; }
    public NetMessageRouter Router { get; } = new();
    public ValidatedInputRouter<TSessionId> InputRouter { get; } = new();
    public event Action<GnsConnection, NetFrame>? FrameReceived;
    public event Action<TSessionId, DisconnectInfo>? SessionDisconnected;
    public void RegisterInput<TInput>(byte opcode, ServerInputGuard<TSessionId, TInput> guard, Func<TSessionId, TInput, bool> handler) where TInput : IMemoryPackable<TInput>
        => this.InputRouter.Register(opcode, guard, handler);
    public void RegisterAuthoritativeInput<TState, TInput>(byte opcode, ServerInputGuard<TSessionId, TInput> guard, AuthoritativeServer<TSessionId, TState, TInput> simulation, Action<TSessionId, TInput>? accepted = null)
        where TInput : IMemoryPackable<TInput>
        => this.InputRouter.RegisterAuthoritative(opcode, guard, simulation, accepted);
    public void RegisterMovementInput<TInput>(byte opcode, ServerInputGuard<TSessionId, TInput> guard, MovementInputGuard<TSessionId> movement, Func<TInput, (float X, float Y)> position, Action<TSessionId, TInput> handler) where TInput : IMemoryPackable<TInput>
        => this.InputRouter.RegisterMovement(opcode, guard, movement, position, handler);
    /// <summary>Registers a client acknowledgement opcode that automatically advances a snapshot delta baseline.</summary>
    public void RegisterSnapshotAcknowledgement<TEntity, TSnapshot>(byte opcode, SnapshotPipeline<TSessionId, TEntity, TSnapshot> pipeline)
        where TEntity : IMemoryPackable<TEntity> where TSnapshot : IMemoryPackable<TSnapshot>
    {
        if (!this.snapshotAcknowledgers.TryAdd(opcode, (client, frame) => pipeline.Acknowledge(client, NetSerializer.Deserialize<TSnapshot>(frame.Payload) ?? throw new InvalidDataException("Invalid snapshot acknowledgement."))))
            throw new InvalidOperationException($"Snapshot acknowledgement opcode {opcode} already registered.");
    }
    public bool AttachSession(TSessionId id, GnsConnection connection)
    {
        this.connectionSessions[connection.Handle.Handle] = id;
        // Attach returns whether the session resumed grace state; admission itself succeeds for
        // both a first connection and a valid resumption.
        bool resumed = this.Sessions.Attach(id, connection);
        if (resumed && this.lifecycleEncoder is not null) this.SendRehydration(id, connection, this.lifecycleOpcode, this.lifecycleEncoder, 0);
        return true;
    }
    /// <summary>Automatically journals lifecycle changes and replays them on a grace-period resumption.</summary>
    public void RegisterLifecycleRehydration(NetworkObjectRegistry<TSessionId> registry, byte opcode, Func<NetworkObjectChange, byte[]> encoder)
    {
        ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(encoder);
        if (this.lifecycleRegistry is not null) throw new InvalidOperationException("Lifecycle rehydration is already registered.");
        this.lifecycleRegistry = registry; this.lifecycleOpcode = opcode; this.lifecycleEncoder = encoder;
        registry.Changed += change => { foreach (TSessionId session in this.Sessions.KnownSessions) this.Sessions.Rehydration.Record(session, change); };
    }
    public int SendRehydration(TSessionId session, byte opcode, Func<NetworkObjectChange, byte[]> encoder, uint tick = 0)
    {
        if (!this.Sessions.TryGet(session, out GnsConnection? connection) || connection is null) return 0;
        return this.SendRehydration(session, connection, opcode, encoder, tick);
    }
    private int SendRehydration(TSessionId session, GnsConnection connection, byte opcode, Func<NetworkObjectChange, byte[]> encoder, uint tick)
    {
        int sent = 0;
        if (this.lifecycleRegistry is not null && this.Sessions.Rehydration.RequiresBaseline(session))
            foreach (NetworkObjectChange change in this.lifecycleRegistry.SnapshotChanges())
                if (this.server.Send(connection, new NetFrame(opcode, tick, encoder(change)).Encode(), ESteamNetworkingSendType.Reliable) == EResult.OK) sent++;
        foreach (NetworkObjectChange change in this.Sessions.Rehydration.Snapshot(session))
            if (this.server.Send(connection, new NetFrame(opcode, tick, encoder(change)).Encode(), ESteamNetworkingSendType.Reliable) == EResult.OK) sent++;
        return sent;
    }
    public bool Admit(GnsConnection connection, string token)
    {
        if (this.LoadShedding is not null && this.LoadProvider is not null && !this.LoadShedding.AllowConnection(this.LoadProvider())) { this.server.Reject(connection, "Server is at capacity"); return false; }
        if (this.admission is null) throw new InvalidOperationException("No ConnectionAdmission was configured.");
        if (!this.admission.TryAdmit(token, out ConnectClaims claims)) { this.server.Reject(connection); return false; }
        if (typeof(TSessionId) != typeof(string)) throw new InvalidOperationException("Token admission requires a string session id or a custom admission adapter.");
        bool attached = this.AttachSession((TSessionId)(object)claims.SessionId, connection);
        if (attached) this.connectionNonces[connection.Handle.Handle] = claims.Nonce;
        else this.admission.Release(claims.Nonce);
        return attached;
    }
    public void DetachSession(GnsConnection connection)
    {
        this.authenticated.Remove(connection.Handle.Handle);
        this.RateLimiter.Remove(connection.Handle.Handle);
        this.heartbeatTrackers.Remove(connection.Handle.Handle); this.heartbeatSentAt.Remove(connection.Handle.Handle);
        if (this.connectionNonces.Remove(connection.Handle.Handle, out string? nonce)) this.admission?.Release(nonce);
        if (this.connectionSessions.Remove(connection.Handle.Handle, out TSessionId? id))
        {
            this.InputRouter.Remove(id);
            bool graceful = this.gracefullyClosed.Remove(connection.Handle.Handle);
            bool heartbeatTimeout = this.heartbeatTimedOut.Remove(connection.Handle.Handle);
            if (graceful) this.Sessions.Remove(id);
            else this.Sessions.Detach(id);
            this.SessionDisconnected?.Invoke(id, new DisconnectInfo(graceful ? GnsDisconnectKind.GracefulQuit : heartbeatTimeout ? GnsDisconnectKind.HeartbeatTimeout : GnsDisconnectKind.TransportClosed, null));
        }
    }
    public int Poll()
    {
        int count = 0;
        foreach (ReceivedMessage message in this.server.Poll())
        {
            try
            {
                if (message.Data.Length > 0 && message.Data[0] == NetBatch.Magic)
                {
                    foreach (NetFrame frame in NetBatch.Decode(message.Data)) { if (this.AcceptFrame(message.Connection, frame)) { if (this.connectionSessions.TryGetValue(message.Connection.Handle.Handle, out TSessionId? id) && this.InputRouter.IsRegistered(frame.Opcode)) { _ = this.InputRouter.Dispatch(id, frame); } else { this.FrameReceived?.Invoke(message.Connection, frame); this.Router.Dispatch(frame); } count++; } }
                }
                else { NetFrame frame = NetFrame.Decode(message.Data); if (this.AcceptFrame(message.Connection, frame)) { if (this.connectionSessions.TryGetValue(message.Connection.Handle.Handle, out TSessionId? id) && this.InputRouter.IsRegistered(frame.Opcode)) { _ = this.InputRouter.Dispatch(id, frame); } else { this.FrameReceived?.Invoke(message.Connection, frame); this.Router.Dispatch(frame); } count++; } }
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or OverflowException)
            { this.server.Reject(message.Connection, "Malformed network frame"); }
            this.Metrics.RecordIn(message.Data.Length); this.MetricsByConnection.GetOrAdd(message.Connection.Handle.Handle, _ => new()).RecordIn(message.Data.Length); GnsTelemetry.RecordInbound(message.Data.Length, message.Connection.Handle.Handle.ToString()); this.Recorder?.Record(false, message.Data, connectionId: message.Connection.Handle.Handle.ToString());
        }
        this.Heartbeats?.Poll();
        this.SendHeartbeatProbes();
        return count;
    }
    private void SendHeartbeatProbes()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (GnsConnection connection in this.server.Connections)
        {
            uint handle = connection.Handle.Handle;
            if (this.heartbeatSentAt.TryGetValue(handle, out DateTimeOffset sent) && now - sent < this.HeartbeatInterval) continue;
            var tracker = this.heartbeatTrackers.TryGetValue(handle, out HeartbeatTracker? existing) ? existing : this.heartbeatTrackers[handle] = new();
            byte[] data = new NetFrame(HeartbeatOpcode, 0, tracker.CreateProbe(this.MetricsByConnection.GetOrAdd(handle, _ => new()))).Encode();
            this.heartbeatSentAt[handle] = now; EResult result = this.server.Send(connection, data, NetChannel.Event.SendType()); this.RecordOutbound(connection, data, NetChannel.Event.SendType(), result == EResult.OK);
        }
    }
    private bool AcceptFrame(GnsConnection connection, NetFrame frame)
    {
        uint handle = connection.Handle.Handle;
        if (!IsControlOpcode(frame.Opcode) && !this.RateLimiter.TryAccept(handle)) { this.server.Reject(connection, "Inbound rate limit exceeded"); return false; }
        if (!this.authenticated.Contains(handle) && this.admission is null && this.RequireApplicationAdmission)
        {
            this.server.Reject(connection, "Connection admission is not configured");
            return false;
        }
        if (this.admission is not null && !this.authenticated.Contains(handle))
        {
            if (frame.Opcode != HandshakeOpcode || !this.Admit(connection, Encoding.UTF8.GetString(frame.Payload))) return false;
            this.authenticated.Add(handle);
            return false;
        }
        if (this.connectionSessions.TryGetValue(handle, out TSessionId? snapshotClient) && this.snapshotAcknowledgers.TryGetValue(frame.Opcode, out Action<TSessionId, NetFrame>? acknowledge))
        {
            try { acknowledge(snapshotClient, frame); } catch (Exception) { this.server.Reject(connection, "Invalid snapshot acknowledgement"); }
            return false;
        }
        if (frame.Opcode == HeartbeatOpcode)
        {
            if (this.Heartbeats is not null && this.connectionSessions.TryGetValue(handle, out TSessionId? id)) this.Heartbeats.Touch(id);
            byte[] acknowledgement = new NetFrame(HeartbeatAckOpcode, frame.Tick, frame.Payload).Encode(); this.RecordOutbound(connection, acknowledgement, NetChannel.Event.SendType(), this.server.Send(connection, acknowledgement, NetChannel.Event.SendType()) == EResult.OK);
            return false;
        }
        if (frame.Opcode == HeartbeatAckOpcode)
        {
            if (this.heartbeatTrackers.TryGetValue(handle, out HeartbeatTracker? tracker)) tracker.Acknowledge(frame.Payload, this.MetricsByConnection.GetOrAdd(handle, _ => new()));
            return false;
        }
        if (frame.Opcode == GracefulDisconnectOpcode)
        {
            this.gracefullyClosed.Add(handle);
            this.server.Reject(connection, "Client quit");
            return false;
        }
        return true;
    }
    public static bool IsControlOpcode(byte opcode) => opcode is HandshakeOpcode or HeartbeatOpcode or HeartbeatAckOpcode or GracefulDisconnectOpcode;
    public EResult Send<T>(GnsConnection connection, byte opcode, uint tick, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
    { byte[] data = new NetFrame(opcode, tick, NetSerializer.Serialize(message)).Encode(); EResult result = this.server.Send(connection, data, sendType); this.RecordOutbound(connection, data, sendType, result == EResult.OK); return result; }
    /// <summary>Routes a serialized message to the currently attached connection for a session.</summary>
    public EResult SendTo<T>(TSessionId session, byte opcode, uint tick, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
        => this.Sessions.TryGet(session, out GnsConnection? connection) && connection is not null
            ? this.Send(connection, opcode, tick, message, sendType)
            : EResult.InvalidParam;
    private void RecordOutbound(GnsConnection connection, byte[] data, ESteamNetworkingSendType sendType, bool delivered)
    { this.Metrics.RecordOut(data.Length, delivered); this.MetricsByConnection.GetOrAdd(connection.Handle.Handle, _ => new()).RecordOut(data.Length, delivered); if (delivered) GnsTelemetry.RecordOutbound(data.Length, connection.Handle.Handle.ToString()); else GnsTelemetry.RecordDrop(connection.Handle.Handle.ToString()); this.Recorder?.Record(true, data, connectionId: connection.Handle.Handle.ToString(), channel: sendType == ESteamNetworkingSendType.Reliable ? NetChannel.Event : NetChannel.State); }
    public void Broadcast<T>(byte opcode, uint tick, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
    { byte[] data = new NetFrame(opcode, tick, NetSerializer.Serialize(message)).Encode(); foreach (GnsConnection connection in this.server.Connections) { EResult result = this.server.Send(connection, data, sendType); this.RecordOutbound(connection, data, sendType, result == EResult.OK); } }
    public EResult SendBatch(GnsConnection connection, NetBatch batch, ESteamNetworkingSendType sendType)
    { var data = batch.Encode(); EResult result = this.server.Send(connection, data, sendType); this.RecordOutbound(connection, data, sendType, result == EResult.OK); return result; }
    public async ValueTask<bool> SendBatchAsync(GnsConnection connection, NetBatch batch, ESteamNetworkingSendType sendType, CancellationToken cancellationToken = default)
    {
        byte[] data = batch.Encode();
        async ValueTask<bool> Deliver() { EResult result = this.server.Send(connection, data, sendType); bool ok = result == EResult.OK; this.RecordOutbound(connection, data, sendType, ok); return ok; }
        if (this.Conditions is not null) { bool delivered = false; if (!await this.Conditions.DeliverAsync(async () => { delivered = await Deliver().ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false)) { this.RecordOutbound(connection, data, sendType, false); return false; } return delivered; }
        return await Deliver().ConfigureAwait(false);
    }
    public EResult SendDelta<T>(GnsConnection connection, byte opcode, uint tick, T state, DeltaCompressor<T> compressor, NetChannel channel)
        where T : IMemoryPackable<T>
        => this.Send(connection, opcode, tick, compressor.Create(connection.Handle.Handle, state), channel.SendType());
    public void SendVisible<TClient, TEntity>(GnsConnection connection, TClient client, IEnumerable<TEntity> entities, InterestManager<TClient, TEntity> interest, Func<TEntity, (float X, float Y)> position, byte opcode, uint tick, NetChannel channel)
        where TClient : notnull where TEntity : IMemoryPackable<TEntity>
    {
        var batch = new NetBatch();
        foreach (TEntity entity in interest.Cull(client, entities, position)) batch.Add(new NetFrame(opcode, tick, NetSerializer.Serialize(entity)));
        if (batch.Count != 0) this.SendBatch(connection, batch, channel.SendType());
    }
    public EResult SendAutomaticSnapshot<TClient, TEntity, TSnapshot>(GnsConnection connection, TClient client, IEnumerable<TEntity> entities, TSnapshot snapshot, SnapshotPipeline<TClient, TEntity, TSnapshot> pipeline, byte entityOpcode, byte snapshotOpcode, uint tick, float relevance, int maxFrames = 64)
        where TClient : notnull where TEntity : IMemoryPackable<TEntity> where TSnapshot : IMemoryPackable<TSnapshot>
    {
        pipeline.Queue(client, entities, snapshot, entityOpcode, snapshotOpcode, tick, relevance);
        var batch = new NetBatch(); foreach (var item in pipeline.Drain(client, maxFrames)) batch.Add(item.Frame);
        return batch.Count == 0 ? EResult.OK : this.SendBatch(connection, batch, NetChannel.State.SendType());
    }
    public EResult Flush(GnsConnection connection, PrioritySendQueue queue, int maxFrames)
    {
        (int Frames, long Bytes) shed = queue.ConsumeShed();
        if (shed.Frames != 0) this.MetricsByConnection.GetOrAdd(connection.Handle.Handle, _ => new()).RecordShed(shed.Frames, shed.Bytes);
        var reliable = new NetBatch();
        var unreliable = new NetBatch();
        foreach (var item in queue.Drain(maxFrames)) (item.Channel == NetChannel.Event ? reliable : unreliable).Add(item.Frame);
        EResult result = EResult.OK;
        if (unreliable.Count != 0) result = this.SendBatch(connection, unreliable, NetChannel.State.SendType());
        if (reliable.Count != 0) result = this.SendBatch(connection, reliable, NetChannel.Event.SendType());
        return result;
    }
}

/// <summary>Application-facing client host: reconnect, typed frames, polling, and routing.</summary>
public sealed class GnsClientHost
{
    private readonly ReconnectableClient transport;
    private readonly string? token;
    private readonly TimeSpan heartbeatInterval;
    private readonly CancellationTokenSource heartbeatCts = new();
    private Task? heartbeatLoop;
    private readonly HeartbeatTracker heartbeatTracker = new();
    public NetworkConditionSimulator? Conditions { get; init; }
    public NetworkRecorder? Recorder { get; init; }
    public ConnectionMetrics Metrics { get; } = new();
    public GnsClientHost(ReconnectableClient transport, string? token = null, TimeSpan? heartbeatInterval = null)
    {
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.token = token;
        this.heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(2);
        this.transport.Connected += _ => { if (this.token is not null) this.transport.TrySend(new NetFrame(GnsServerHost<string>.HandshakeOpcode, 0, Encoding.UTF8.GetBytes(this.token)).Encode(), NetChannel.Event.SendType()); };
    }
    public NetMessageRouter Router { get; } = new();
    public event Action<NetFrame>? FrameReceived;
    public int Poll()
    {
        int count = 0;
        foreach (ReceivedMessage message in this.transport.Poll())
        {
            this.Metrics.RecordIn(message.Data.Length); this.Recorder?.Record(false, message.Data, connectionId: "client");
            if (message.Data.Length > 0 && message.Data[0] == NetBatch.Magic)
            {
                foreach (NetFrame frame in NetBatch.Decode(message.Data)) { if (this.DispatchFrame(frame)) count++; }
            }
            else { NetFrame frame = NetFrame.Decode(message.Data); if (this.DispatchFrame(frame)) count++; }
        }
        return count;
    }
    public bool Send<T>(byte opcode, uint tick, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
    { var data = new NetFrame(opcode, tick, NetSerializer.Serialize(message)).Encode(); bool ok = this.transport.TrySend(data, sendType); this.Metrics.RecordOut(data.Length, ok); this.Recorder?.Record(true, data, connectionId: "client", channel: sendType == ESteamNetworkingSendType.Reliable ? NetChannel.Event : NetChannel.State); return ok; }
    public async ValueTask<bool> SendAsync<T>(byte opcode, uint tick, T message, NetChannel channel, CancellationToken cancellationToken = default)
        where T : IMemoryPackable<T>
    {
        byte[] data = new NetFrame(opcode, tick, NetSerializer.Serialize(message)).Encode();
        async ValueTask<bool> Deliver() { bool ok = this.transport.TrySend(data, channel.SendType()); this.Metrics.RecordOut(data.Length, ok); this.Recorder?.Record(true, data, connectionId: "client", channel: channel); return ok; }
        if (this.Conditions is not null) { bool delivered = false; if (!await this.Conditions.DeliverAsync(async () => { delivered = await Deliver().ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false)) { this.Metrics.RecordOut(data.Length, false); return false; } return delivered; }
        return await Deliver().ConfigureAwait(false);
    }
    public void Start() { this.transport.Start(); this.heartbeatLoop ??= Task.Run(() => this.RunHeartbeatsAsync(this.heartbeatCts.Token)); }
    public bool SendBatch(NetBatch batch, NetChannel channel) { byte[] data = batch.Encode(); bool ok = this.transport.TrySend(data, channel.SendType()); this.Metrics.RecordOut(data.Length, ok); this.Recorder?.Record(true, data, connectionId: "client", channel: channel); return ok; }
    public bool SendHeartbeat()
    {
        byte[] data = new NetFrame(GnsServerHost<string>.HeartbeatOpcode, 0, this.heartbeatTracker.CreateProbe(this.Metrics)).Encode();
        bool ok = this.transport.TrySend(data, NetChannel.Event.SendType()); this.Metrics.RecordOut(data.Length, ok); this.Recorder?.Record(true, data, connectionId: "client", channel: NetChannel.Event); return ok;
    }
    public void Disconnect() => this.transport.DisconnectGracefully();
    public async ValueTask DisposeAsync()
    {
        this.heartbeatCts.Cancel();
        if (this.heartbeatLoop is not null) try { await this.heartbeatLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await this.transport.DisposeAsync().ConfigureAwait(false);
        this.heartbeatCts.Dispose();
    }
    private async Task RunHeartbeatsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(this.heartbeatInterval, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            this.SendHeartbeat();
        }
    }
    private bool DispatchFrame(NetFrame frame)
    {
        if (frame.Opcode == GnsServerHost<string>.HeartbeatOpcode)
        {
            byte[] acknowledgement = new NetFrame(GnsServerHost<string>.HeartbeatAckOpcode, frame.Tick, frame.Payload).Encode(); bool ok = this.transport.TrySend(acknowledgement, NetChannel.Event.SendType()); this.Metrics.RecordOut(acknowledgement.Length, ok); this.Recorder?.Record(true, acknowledgement, connectionId: "client", channel: NetChannel.Event); return false;
        }
        if (frame.Opcode == GnsServerHost<string>.HeartbeatAckOpcode) return this.heartbeatTracker.Acknowledge(frame.Payload, this.Metrics);
        this.FrameReceived?.Invoke(frame); this.Router.Dispatch(frame); return true;
    }
}
