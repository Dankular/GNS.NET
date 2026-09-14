namespace GnsNet;

/// <summary>Automatically orders authoritative spawn/despawn/ownership records by AOI visibility.</summary>
public sealed class LifecycleReplicationScheduler<TClientId> where TClientId : notnull
{
    private readonly NetworkObjectRegistry<TClientId> registry;
    private readonly ObserverTracker<TClientId, long> observers = new();
    private readonly Dictionary<TClientId, PrioritySendQueue> queues = new();
    private readonly Func<NetworkObjectChange, byte[]> encode;
    private readonly byte opcode;
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
