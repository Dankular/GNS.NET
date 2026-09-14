namespace GnsNet;

/// <summary>Automatically orders authoritative spawn/despawn/ownership records by AOI visibility.</summary>
public sealed class LifecycleReplicationScheduler<TClientId> where TClientId : notnull
{
    private readonly NetworkObjectRegistry<TClientId> registry;
    private readonly ObserverTracker<TClientId, long> observers = new();
    private readonly Dictionary<TClientId, PrioritySendQueue> queues = new();
    private readonly Func<NetworkObjectChange, byte[]> encode;
    private readonly byte opcode;
    private Func<TClientId, IEnumerable<long>>? visibilityProvider;
    public LifecycleReplicationScheduler(NetworkObjectRegistry<TClientId> registry, byte opcode, Func<NetworkObjectChange, byte[]> encode)
    { this.registry = registry ?? throw new ArgumentNullException(nameof(registry)); this.opcode = opcode; this.encode = encode ?? throw new ArgumentNullException(nameof(encode)); this.registry.Changed += this.OnChanged; this.observers.Entered += this.OnEntered; this.observers.Left += this.OnLeft; }
    public void AddClient(TClientId client) { this.observers.Update(client, Array.Empty<long>()); this.queues.TryAdd(client, new PrioritySendQueue()); }
    public void RemoveClient(TClientId client) { this.observers.Remove(client); this.queues.Remove(client); }
    /// <summary>Updates AOI membership. Enter emits spawn before later state; leave emits despawn.</summary>
    public void UpdateVisibility(TClientId client, IEnumerable<long> visible, uint tick)
    {
        if (!this.queues.ContainsKey(client)) throw new InvalidOperationException("Client is not registered.");
        this.currentTick = tick; this.observers.Update(client, visible);
    }
    /// <summary>Registers the AOI query used to refresh every observer set during <see cref="Tick"/>.</summary>
    public void ConfigureAutomaticVisibility(Func<TClientId, IEnumerable<long>> provider)
        => this.visibilityProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    /// <summary>Runs the AOI pass and automatically delivers observer enter/leave lifecycle records.</summary>
    public void Tick(uint tick)
    {
        if (this.visibilityProvider is null) throw new InvalidOperationException("Automatic visibility is not configured.");
        this.currentTick = tick;
        foreach (TClientId client in this.queues.Keys.ToArray())
        {
            IEnumerable<long> visible = this.visibilityProvider(client) ?? throw new InvalidOperationException("The automatic visibility provider returned null.");
            this.observers.Update(client, visible);
        }
    }
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(TClientId client, int maxFrames)
        => this.queues.TryGetValue(client, out PrioritySendQueue? queue) ? queue.Drain(maxFrames) : [];
    private uint currentTick;
    private void OnEntered(TClientId client, long objectId)
    {
        if (!this.registry.TryGet(objectId, out NetworkObjectDescriptor descriptor)) return;
        this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(new(NetworkObjectChangeKind.Spawned, descriptor))), NetChannel.Event, 1);
    }
    private void OnLeft(TClientId client, long objectId)
    {
        if (!this.registry.TryGet(objectId, out NetworkObjectDescriptor descriptor)) return;
        this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(new(NetworkObjectChangeKind.Despawned, descriptor, Reason: "left AOI"))), NetChannel.Event, 1);
    }
    private void OnChanged(NetworkObjectChange change)
    {
        foreach (TClientId client in this.queues.Keys.ToArray())
        {
            bool visible = this.observers.Current(client).Contains(change.Object.ObjectId);
            if (change.Kind == NetworkObjectChangeKind.Spawned) visible = this.observers.Current(client).Contains(change.Object.ObjectId);
            if (!visible) continue;
            this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(change)), NetChannel.Event, 1);
        }
    }
}
