namespace GnsNet;

using MemoryPack;

public enum NetworkObjectChangeKind { Spawned, Despawned, OwnershipTransferred }
public readonly record struct NetworkObjectDescriptor(long ObjectId, int TypeId, string OwnerId, uint SpawnTick);
public readonly record struct NetworkObjectChange(NetworkObjectChangeKind Kind, NetworkObjectDescriptor Object, string? PreviousOwner = null, string? Reason = null);

/// <summary>Authoritative registry for stable network object identity and ownership changes.</summary>
public sealed class NetworkObjectRegistry<TClientId> where TClientId : notnull
{
    private readonly Dictionary<long, NetworkObjectDescriptor> objects = new();
    private long nextId = 1;
    public IReadOnlyCollection<NetworkObjectDescriptor> Objects => this.objects.Values.ToArray();
    public event Action<NetworkObjectChange>? Changed;
    public IReadOnlyCollection<NetworkObjectChange> SnapshotChanges()
        => this.objects.Values.OrderBy(x => x.ObjectId).Select(x => new NetworkObjectChange(NetworkObjectChangeKind.Spawned, x)).ToArray();

    public NetworkObjectDescriptor Spawn(int typeId, TClientId owner, uint tick)
    {
        if (typeId < 0) throw new ArgumentOutOfRangeException(nameof(typeId));
        string ownerId = owner.ToString() ?? throw new ArgumentException("Owner cannot be null.", nameof(owner));
        var descriptor = new NetworkObjectDescriptor(this.nextId++, typeId, ownerId, tick);
        this.objects.Add(descriptor.ObjectId, descriptor); this.Changed?.Invoke(new(NetworkObjectChangeKind.Spawned, descriptor)); return descriptor;
    }
    public bool Despawn(long objectId, string reason)
    {
        if (!this.objects.Remove(objectId, out NetworkObjectDescriptor descriptor)) return false;
        this.Changed?.Invoke(new(NetworkObjectChangeKind.Despawned, descriptor, Reason: reason)); return true;
    }
    public bool TransferOwnership(long objectId, TClientId owner)
    {
        if (!this.objects.TryGetValue(objectId, out NetworkObjectDescriptor current)) return false;
        string ownerId = owner.ToString() ?? throw new ArgumentException("Owner cannot be null.", nameof(owner));
        if (current.OwnerId == ownerId) return true;
        var updated = current with { OwnerId = ownerId }; this.objects[objectId] = updated;
        this.Changed?.Invoke(new(NetworkObjectChangeKind.OwnershipTransferred, updated, current.OwnerId)); return true;
    }
    public bool TryGet(long objectId, out NetworkObjectDescriptor descriptor) => this.objects.TryGetValue(objectId, out descriptor);
    /// <summary>Applies an authoritative lifecycle record idempotently on a reconnecting client.</summary>
    public bool Apply(NetworkObjectChange change)
    {
        switch (change.Kind)
        {
            case NetworkObjectChangeKind.Spawned:
                if (this.objects.TryGetValue(change.Object.ObjectId, out NetworkObjectDescriptor existing)) return existing == change.Object;
                this.objects[change.Object.ObjectId] = change.Object; this.nextId = Math.Max(this.nextId, change.Object.ObjectId + 1); return true;
            case NetworkObjectChangeKind.Despawned:
                return !this.objects.ContainsKey(change.Object.ObjectId) || this.objects.Remove(change.Object.ObjectId);
            case NetworkObjectChangeKind.OwnershipTransferred:
                if (!this.objects.TryGetValue(change.Object.ObjectId, out NetworkObjectDescriptor current)) return false;
                if (current == change.Object) return true;
                if (current.TypeId != change.Object.TypeId || current.SpawnTick != change.Object.SpawnTick) return false;
                this.objects[change.Object.ObjectId] = change.Object; return true;
            default: return false;
        }
    }
}

public enum RoomPhase { Lobby, Ready, Starting, InGame, Draining, Ended }
public readonly record struct RoomMember<TClientId>(TClientId Client, bool Ready) where TClientId : notnull;
public readonly record struct RoomSceneTransition(string From, string To, uint Tick);
public enum RoomLifecycleEventKind { Joined, Left, ReadyChanged, Started, GameBegan, SceneChanged, Draining, Ended }
public readonly record struct RoomLifecycleEvent<TClientId>(RoomLifecycleEventKind Kind, TClientId? Client, RoomPhase Phase, string Scene, uint Tick) where TClientId : notnull;
public readonly record struct RoomStateSnapshot<TClientId>(RoomPhase Phase, string Scene, IReadOnlyCollection<RoomMember<TClientId>> Members, IReadOnlyCollection<NetworkObjectDescriptor> Objects) where TClientId : notnull;

/// <summary>Deterministic room lifecycle with ready/start locking and late-join policy.</summary>
public sealed class RoomLifecycle<TClientId> where TClientId : notnull
{
    private readonly Dictionary<TClientId, bool> members = new();
    public RoomPhase Phase { get; private set; } = RoomPhase.Lobby;
    public int MaxPlayers { get; }
    public bool AllowLateJoin { get; init; }
    public string CurrentScene { get; private set; } = "default";
    public event Action<RoomSceneTransition>? SceneChanged;
    public event Action<RoomLifecycleEvent<TClientId>>? LifecycleChanged;
    public IReadOnlyCollection<RoomMember<TClientId>> Members => this.members.Select(x => new RoomMember<TClientId>(x.Key, x.Value)).ToArray();
    public RoomLifecycle(int maxPlayers = 16) { if (maxPlayers < 2) throw new ArgumentOutOfRangeException(nameof(maxPlayers)); this.MaxPlayers = maxPlayers; }
    public bool Join(TClientId client)
    {
        if (this.members.ContainsKey(client)) return true;
        if (this.members.Count >= this.MaxPlayers || (this.Phase is not RoomPhase.Lobby and not RoomPhase.Ready && !this.AllowLateJoin)) return false;
        this.members.Add(client, false); this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.Joined, client, this.Phase, this.CurrentScene, 0)); return true;
    }
    public bool Leave(TClientId client)
    {
        if (!this.members.Remove(client)) return false;
        this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.Left, client, this.Phase, this.CurrentScene, 0)); return true;
    }
    public bool SetReady(TClientId client, bool ready)
    {
        if (this.Phase is not (RoomPhase.Lobby or RoomPhase.Ready) || !this.members.ContainsKey(client)) return false;
        this.members[client] = ready; this.Phase = this.members.Count > 0 && this.members.Values.All(x => x) ? RoomPhase.Ready : RoomPhase.Lobby;
        this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.ReadyChanged, client, this.Phase, this.CurrentScene, 0)); return true;
    }
    public bool Start()
    {
        if (this.Phase != RoomPhase.Ready) return false;
        this.Phase = RoomPhase.Starting; this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.Started, default, this.Phase, this.CurrentScene, 0)); return true;
    }
    public void BeginGame() { if (this.Phase != RoomPhase.Starting) throw new InvalidOperationException("Room must be starting."); this.Phase = RoomPhase.InGame; this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.GameBegan, default, this.Phase, this.CurrentScene, 0)); }
    public bool TransitionScene(string scene, uint tick)
    {
        if (string.IsNullOrWhiteSpace(scene) || this.Phase is RoomPhase.Ended or RoomPhase.Draining || string.Equals(scene, this.CurrentScene, StringComparison.Ordinal)) return false;
        string previous = this.CurrentScene; this.CurrentScene = scene; this.SceneChanged?.Invoke(new(previous, scene, tick)); this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.SceneChanged, default, this.Phase, scene, tick)); return true;
    }
    public void Drain() { if (this.Phase is RoomPhase.Ended or RoomPhase.Draining) return; this.Phase = RoomPhase.Draining; this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.Draining, default, this.Phase, this.CurrentScene, 0)); }
    public void Ended() { if (this.Phase == RoomPhase.Ended) return; this.Phase = RoomPhase.Ended; this.LifecycleChanged?.Invoke(new(RoomLifecycleEventKind.Ended, default, this.Phase, this.CurrentScene, 0)); }

    public RoomStateSnapshot<TClientId> Snapshot(NetworkObjectRegistry<TClientId> registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new(this.Phase, this.CurrentScene, this.Members, registry.Objects);
    }
}

/// <summary>Composes room membership, authoritative object identity, and late-join snapshots.</summary>
public sealed class RoomSessionCoordinator<TClientId> where TClientId : notnull
{
    public RoomLifecycle<TClientId> Room { get; }
    public NetworkObjectRegistry<TClientId> Objects { get; }
    public event Action<RoomLifecycleEvent<TClientId>>? LifecycleChanged;
    public RoomSessionCoordinator(RoomLifecycle<TClientId> room, NetworkObjectRegistry<TClientId> objects)
    {
        Room = room ?? throw new ArgumentNullException(nameof(room)); Objects = objects ?? throw new ArgumentNullException(nameof(objects));
        Room.LifecycleChanged += e => LifecycleChanged?.Invoke(e);
    }
    public bool Join(TClientId client) => Room.Join(client);
    public bool Leave(TClientId client) => Room.Leave(client);
    public bool SetReady(TClientId client, bool ready) => Room.SetReady(client, ready);
    public bool Start() => Room.Start();
    public void BeginGame() => Room.BeginGame();
    public bool TransitionScene(string scene, uint tick) => Room.TransitionScene(scene, tick);
    public void Drain() => Room.Drain();
    public void End() => Room.Ended();
    public RoomStateSnapshot<TClientId> GetLateJoinState(TClientId client)
    {
        if (!Room.Members.Any(x => EqualityComparer<TClientId>.Default.Equals(x.Client, client))) throw new InvalidOperationException("Client is not a room member.");
        if (Room.Phase is not (RoomPhase.Starting or RoomPhase.InGame)) throw new InvalidOperationException("Late-join state is available only after the room starts.");
        return Room.Snapshot(Objects);
    }
}

public enum RpcAuthority { ServerOnly, OwnerOnly, AnyAuthenticated }
public readonly record struct RpcEndpointCapability(string Endpoint, RpcAuthority Authority);
public readonly record struct RpcRequest(Guid RequestId, string Endpoint, long? ObjectId, string Caller, byte[] Payload, uint Tick);
public readonly record struct RpcResponse(Guid RequestId, bool Accepted, byte[]? Payload, string? Error = null);

[MemoryPackable]
public partial record RpcRequestEnvelope(Guid RequestId, string Endpoint, long? ObjectId, byte[] Payload);
[MemoryPackable]
public partial record RpcResponseEnvelope(Guid RequestId, bool Accepted, byte[]? Payload, string? Error);

/// <summary>Routes server-originated RPC invocations to registered client handlers.</summary>
public sealed class ClientRpcRouter
{
    private readonly Dictionary<byte, (string Endpoint, Action<RpcRequestEnvelope, uint> Handler)> handlers = new();
    public void Register<T>(byte opcode, string endpoint, Action<RpcRequestEnvelope, T, uint> handler) where T : IMemoryPackable<T>
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Endpoint is required.", nameof(endpoint));
        ArgumentNullException.ThrowIfNull(handler);
        if (!this.handlers.TryAdd(opcode, (endpoint, (request, tick) =>
        {
            T command = NetSerializer.Deserialize<T>(request.Payload) ?? throw new InvalidDataException("Invalid client RPC payload.");
            handler(request, command, tick);
        }))) throw new InvalidOperationException($"Client RPC opcode {opcode} is already registered.");
    }
    public bool Dispatch(byte opcode, uint tick, RpcRequestEnvelope request)
    {
        if (!this.handlers.TryGetValue(opcode, out (string Endpoint, Action<RpcRequestEnvelope, uint> Handler) handler) ||
            !string.Equals(handler.Endpoint, request.Endpoint, StringComparison.Ordinal)) return false;
        try { handler.Handler(request, tick); return true; }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or MemoryPackSerializationException) { return false; }
    }
}

/// <summary>Registers gameplay commands/RPCs with explicit authority and correlated responses.</summary>
public sealed class RpcRouter
{
    private sealed record Endpoint(RpcAuthority Authority, Func<RpcRequest, RpcResponse> Handler);
    private readonly Dictionary<string, Endpoint> endpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> owners = new();
    public void Register(string endpoint, RpcAuthority authority, Func<RpcRequest, RpcResponse> handler)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) throw new ArgumentException("Endpoint is required.", nameof(endpoint));
        if (!this.endpoints.TryAdd(endpoint, new(authority, handler))) throw new InvalidOperationException($"RPC '{endpoint}' is already registered.");
    }
    public void SetOwner(long objectId, string owner) => this.owners[objectId] = owner;
    /// <summary>Returns the stable endpoint contract for capability negotiation.</summary>
    public IReadOnlyCollection<RpcEndpointCapability> DescribeCapabilities()
        => this.endpoints.OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new RpcEndpointCapability(x.Key, x.Value.Authority)).ToArray();

    public bool TryGetCapability(string endpoint, out RpcEndpointCapability capability)
    {
        if (this.endpoints.TryGetValue(endpoint, out Endpoint? registered))
        {
            capability = new(endpoint, registered.Authority);
            return true;
        }

        capability = default;
        return false;
    }
    /// <summary>Registers a typed MemoryPack command while retaining the endpoint authority policy.</summary>
    public void RegisterCommand<TRequest, TResponse>(string endpoint, RpcAuthority authority, Func<RpcRequest, TRequest, TResponse> handler)
        where TRequest : IMemoryPackable<TRequest> where TResponse : IMemoryPackable<TResponse>
    {
        ArgumentNullException.ThrowIfNull(handler);
        this.Register(endpoint, authority, request =>
        {
            try
            {
                TRequest? command = NetSerializer.Deserialize<TRequest>(request.Payload);
                if (command is null) return new RpcResponse(request.RequestId, false, null, "Invalid command payload.");
                return new RpcResponse(request.RequestId, true, NetSerializer.Serialize(handler(request, command)));
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or MemoryPackSerializationException)
            { return new RpcResponse(request.RequestId, false, null, "Command handler rejected the request."); }
        });
    }
    public RpcResponse Dispatch(RpcRequest request, bool callerIsServer = false)
    {
        if (!this.endpoints.TryGetValue(request.Endpoint, out Endpoint? endpoint)) return new(request.RequestId, false, null, "Unknown endpoint.");
        bool allowed = endpoint.Authority == RpcAuthority.AnyAuthenticated || (endpoint.Authority == RpcAuthority.ServerOnly && callerIsServer) ||
            (endpoint.Authority == RpcAuthority.OwnerOnly && request.ObjectId is long id && this.owners.TryGetValue(id, out string? owner) && owner == request.Caller);
        return allowed ? endpoint.Handler(request) : new(request.RequestId, false, null, "Authority denied.");
    }
}

/// <summary>Client-side request correlation with bounded pending requests and timeouts.</summary>
public sealed class RpcRequestTracker
{
    private readonly Dictionary<Guid, TaskCompletionSource<RpcResponse>> pending = new();
    private readonly object sync = new();
    public int PendingCount { get { lock (this.sync) return this.pending.Count; } }
    public (Guid RequestId, Task<RpcResponse> Completion) Create(CancellationToken cancellationToken = default)
    {
        Guid id = Guid.NewGuid(); var completion = new TaskCompletionSource<RpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.sync) this.pending.Add(id, completion);
        if (cancellationToken.CanBeCanceled) cancellationToken.Register(() => Cancel(id));
        return (id, completion.Task);
    }
    public (Guid RequestId, Task<RpcResponse> Completion) Create(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        (Guid id, Task<RpcResponse> completion) request = this.Create(cancellationToken);
        _ = Task.Delay(timeout).ContinueWith(_ => this.Cancel(request.id), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return request;
    }
    public bool Complete(RpcResponse response) { lock (this.sync) if (!this.pending.Remove(response.RequestId, out var completion)) return false; else return completion.TrySetResult(response); }
    public bool Cancel(Guid id) { lock (this.sync) if (!this.pending.Remove(id, out var completion)) return false; else return completion.TrySetCanceled(); }
}
