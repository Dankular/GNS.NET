namespace GnsNet;

using MemoryPack;

/// <summary>Mandatory per-client snapshot pipeline: AOI, delta baseline, priority, batching, channel.</summary>
public sealed class SnapshotPipeline<TClientId, TEntity, TSnapshot> where TClientId : notnull where TEntity : IMemoryPackable<TEntity> where TSnapshot : IMemoryPackable<TSnapshot>
{
    private readonly InterestManager<TClientId, TEntity> interest;
    private readonly DeltaCompressor<TSnapshot> delta;
    private readonly Func<TEntity, (float X, float Y)> position;
    private readonly Dictionary<TClientId, PrioritySendQueue> queues = new();
    private readonly Dictionary<TClientId, uint> lastSnapshotTicks = new();
    public SnapshotPipeline(InterestManager<TClientId, TEntity> interest, DeltaCompressor<TSnapshot> delta, Func<TEntity, (float X, float Y)> position) { this.interest = interest; this.delta = delta; this.position = position; }
    public void Queue(TClientId client, IEnumerable<TEntity> entities, TSnapshot snapshot, byte entityOpcode, byte snapshotOpcode, uint tick, float relevance)
    {
        if (float.IsNaN(relevance) || relevance < 0) throw new ArgumentOutOfRangeException(nameof(relevance));
        int interval = Math.Clamp((int)MathF.Ceiling(1f / MathF.Max(relevance, 0.125f)), 1, 8);
        if (this.lastSnapshotTicks.TryGetValue(client, out uint last))
        {
            if (!TickSequence.IsNewer(last, tick)) return;
            if (tick - last < interval) return;
        }
        this.lastSnapshotTicks[client] = tick;
        if (!this.queues.TryGetValue(client, out PrioritySendQueue? queue)) this.queues[client] = queue = new();
        foreach (TEntity entity in this.interest.Cull(client, entities, this.position)) queue.Enqueue(new NetFrame(entityOpcode, tick, NetSerializer.Serialize(entity)), NetChannel.State, relevance);
        queue.Enqueue(new NetFrame(snapshotOpcode, tick, NetSerializer.Serialize(this.delta.Create(client!, snapshot))), NetChannel.State, relevance);
    }
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(TClientId client, int maxFrames) => this.queues.TryGetValue(client, out PrioritySendQueue? queue) ? queue.Drain(maxFrames) : [];
    public void Acknowledge(TClientId client, TSnapshot authoritativeSnapshot) => this.delta.Acknowledge(client!, authoritativeSnapshot);
    public void Remove(TClientId client) { this.queues.Remove(client); this.lastSnapshotTicks.Remove(client); this.delta.Remove(client!); this.interest.Remove(client); }
}

/// <summary>Owns the per-client snapshot pass so game code submits one authoritative world per tick.</summary>
public sealed class AutomaticSnapshotScheduler<TClientId, TEntity, TSnapshot> where TClientId : notnull where TEntity : IMemoryPackable<TEntity> where TSnapshot : IMemoryPackable<TSnapshot>
{
    private readonly SnapshotPipeline<TClientId, TEntity, TSnapshot> pipeline;
    private readonly HashSet<TClientId> clients = new();
    public AutomaticSnapshotScheduler(SnapshotPipeline<TClientId, TEntity, TSnapshot> pipeline) => this.pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    public void AddClient(TClientId client) => this.clients.Add(client);
    /// <summary>Registers a late joiner and queues its immediate authoritative world transfer.</summary>
    public void AddClient(TClientId client, IEnumerable<TEntity> entities, TSnapshot snapshot, byte entityOpcode, byte snapshotOpcode, uint tick, float relevance = 1f)
    {
        this.AddClient(client);
        this.pipeline.Queue(client, entities, snapshot, entityOpcode, snapshotOpcode, tick, relevance);
    }
    public void RemoveClient(TClientId client) { this.clients.Remove(client); this.pipeline.Remove(client); }
    public void Publish(IEnumerable<TEntity> entities, Func<TClientId, TSnapshot> snapshot, Func<TClientId, float> relevance, byte entityOpcode, byte snapshotOpcode, uint tick)
    {
        TEntity[] world = entities.ToArray(); foreach (TClientId client in this.clients) this.pipeline.Queue(client, world, snapshot(client), entityOpcode, snapshotOpcode, tick, relevance(client));
    }
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(TClientId client, int maxFrames) => this.pipeline.Drain(client, maxFrames);
    public void Acknowledge(TClientId client, TSnapshot snapshot) => this.pipeline.Acknowledge(client, snapshot);
}

/// <summary>Registered input endpoint that always runs rate and validation checks before a handler.</summary>
public sealed class ValidatedInputRouter<TClientId> where TClientId : notnull
{
    private readonly Dictionary<byte, Func<TClientId, NetFrame, bool>> handlers = new();
    private readonly Dictionary<TClientId, uint> lastTicks = new();
    private readonly object sequenceSync = new();
    public void Register<TInput>(byte opcode, ServerInputGuard<TClientId, TInput> guard, Func<TClientId, TInput, bool> handler) where TInput : IMemoryPackable<TInput>
        => this.RegisterCore(opcode, guard, (client, _, input) => handler(client, input));

    /// <summary>Registers validated input and forwards its network tick into an authoritative server.</summary>
    public void RegisterAuthoritative<TState, TInput>(byte opcode, ServerInputGuard<TClientId, TInput> guard, AuthoritativeServer<TClientId, TState, TInput> simulation, Action<TClientId, TInput>? accepted = null)
        where TInput : IMemoryPackable<TInput>
        => this.RegisterCore(opcode, guard, (client, tick, input) => { simulation.SubmitValidatedInput(client, tick, input); accepted?.Invoke(client, input); });

    private void RegisterCore<TInput>(byte opcode, ServerInputGuard<TClientId, TInput> guard, Action<TClientId, uint, TInput> handler) where TInput : IMemoryPackable<TInput>
    {
        if (!this.handlers.TryAdd(opcode, (client, frame) =>
        {
            TInput? input;
            try { input = NetSerializer.Deserialize<TInput>(frame.Payload); }
            catch (Exception) { return false; }
            if (input is null || !this.TryAcceptSequence(client, frame.Tick, () => guard.TryAccept(client, input))) return false;
            handler(client, frame.Tick, input); return true;
        })) throw new InvalidOperationException($"Input opcode {opcode} already registered.");
    }
    private bool TryAcceptSequence(TClientId client, uint tick, Func<bool> accept)
    {
        lock (this.sequenceSync)
        {
            if (this.lastTicks.TryGetValue(client, out uint last) && !TickSequence.IsNewer(last, tick)) return false;
            if (!accept()) return false;
            this.lastTicks[client] = tick;
            return true;
        }
    }
    public void RegisterMovement<TInput>(byte opcode, ServerInputGuard<TClientId, TInput> guard, MovementInputGuard<TClientId> movement, Func<TInput, (float X, float Y)> position, Action<TClientId, TInput> handler) where TInput : IMemoryPackable<TInput>
    {
        this.Register(opcode, guard, (client, input) => { if (!movement.Accept(client, position(input))) return false; handler(client, input); return true; });
    }
    public bool Dispatch(TClientId client, NetFrame frame) => this.handlers.TryGetValue(frame.Opcode, out var handler) && handler(client, frame);
    public bool IsRegistered(byte opcode) => this.handlers.ContainsKey(opcode);
    public void Remove(TClientId client) { lock (this.sequenceSync) this.lastTicks.Remove(client); }
}
