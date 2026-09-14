namespace GnsNet;

/// <summary>Automatically orders authoritative spawn/despawn/ownership records by AOI visibility.</summary>
public sealed class LifecycleReplicationScheduler<TClientId> where TClientId : notnull
{
    private readonly NetworkObjectRegistry<TClientId> registry;
    private readonly ObserverTracker<TClientId, long> observers = new();
    private readonly Dictionary<TClientId, PrioritySendQueue> queues = new();
    private readonly Dictionary<long, NetworkObjectDescriptor> descriptors = new();
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
    /// <summary>
    /// Configures automatic visibility from a spatial AOI and authoritative scene/team/owner
    /// policy. The world is rebuilt before each observer query so lifecycle enter/leave records
    /// follow the same filters used by gameplay replication.
    /// </summary>
    public void ConfigureAutomaticVisibility<TEntity>(
        SpatialHashInterestManager<TClientId, TEntity> interest,
        Func<IEnumerable<TEntity>> world,
        Func<TEntity, long> objectId,
        Func<TEntity, (float X, float Y)> position,
        Func<TEntity, string?> scene,
        Func<TEntity, string?> team,
        Func<TEntity, string?> owner,
        Func<TClientId, string?> clientScene,
        Func<TClientId, string?> clientTeam,
        Func<TClientId, string?> clientOwner,
        Func<TEntity, bool>? visibility = null,
        Func<TEntity, bool>? occlusion = null)
    {
        ArgumentNullException.ThrowIfNull(interest); ArgumentNullException.ThrowIfNull(world); ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(position); ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(owner); ArgumentNullException.ThrowIfNull(clientScene); ArgumentNullException.ThrowIfNull(clientTeam); ArgumentNullException.ThrowIfNull(clientOwner);
        this.ConfigureAutomaticVisibility(client =>
        {
            TEntity[] snapshot = world().ToArray();
            interest.Rebuild(snapshot, position);
            return interest.Cull(client, position, scene, team, owner, clientScene(client), clientTeam(client), clientOwner(client), visibility, occlusion).Select(objectId);
        });
    }
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
        if (!this.TryGetDescriptor(objectId, out NetworkObjectDescriptor descriptor)) return;
        this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(new(NetworkObjectChangeKind.Spawned, descriptor))), NetChannel.Event, 1);
    }
    private void OnLeft(TClientId client, long objectId)
    {
        if (!this.TryGetDescriptor(objectId, out NetworkObjectDescriptor descriptor)) return;
        this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(new(NetworkObjectChangeKind.Despawned, descriptor, Reason: "left AOI"))), NetChannel.Event, 1);
    }
    private void OnChanged(NetworkObjectChange change)
    {
        if (change.Kind is NetworkObjectChangeKind.Spawned or NetworkObjectChangeKind.OwnershipTransferred)
            this.descriptors[change.Object.ObjectId] = change.Object;
        foreach (TClientId client in this.queues.Keys.ToArray())
        {
            bool visible = this.observers.Current(client).Contains(change.Object.ObjectId);
            if (visible) this.queues[client].Enqueue(new NetFrame(this.opcode, this.currentTick, this.encode(change)), NetChannel.Event, 1);
        }
        if (change.Kind == NetworkObjectChangeKind.Despawned)
        {
            this.observers.Forget(change.Object.ObjectId);
            this.descriptors.Remove(change.Object.ObjectId);
        }
    }

    private bool TryGetDescriptor(long objectId, out NetworkObjectDescriptor descriptor)
        => this.registry.TryGet(objectId, out descriptor) || this.descriptors.TryGetValue(objectId, out descriptor);
}
