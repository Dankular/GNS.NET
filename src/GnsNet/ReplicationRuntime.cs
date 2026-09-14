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

/// <summary>Deterministic room lifecycle with ready/start locking and late-join policy.</summary>
public sealed class RoomLifecycle<TClientId> where TClientId : notnull
{
    private readonly Dictionary<TClientId, bool> members = new();
    public RoomPhase Phase { get; private set; } = RoomPhase.Lobby;
    public int MaxPlayers { get; }
    public bool AllowLateJoin { get; init; }
    public IReadOnlyCollection<RoomMember<TClientId>> Members => this.members.Select(x => new RoomMember<TClientId>(x.Key, x.Value)).ToArray();
    public RoomLifecycle(int maxPlayers = 16) { if (maxPlayers < 2) throw new ArgumentOutOfRangeException(nameof(maxPlayers)); this.MaxPlayers = maxPlayers; }
    public bool Join(TClientId client)
    {
        if (this.members.ContainsKey(client)) return true;
        if (this.members.Count >= this.MaxPlayers || (this.Phase is not RoomPhase.Lobby and not RoomPhase.Ready && !this.AllowLateJoin)) return false;
        this.members.Add(client, false); return true;
    }
    public bool Leave(TClientId client) => this.members.Remove(client);
    public bool SetReady(TClientId client, bool ready)
    {
        if (this.Phase is not (RoomPhase.Lobby or RoomPhase.Ready) || !this.members.ContainsKey(client)) return false;
        this.members[client] = ready; this.Phase = this.members.Count > 0 && this.members.Values.All(x => x) ? RoomPhase.Ready : RoomPhase.Lobby; return true;
    }
    public bool Start()
    {
        if (this.Phase != RoomPhase.Ready) return false;
        this.Phase = RoomPhase.Starting; return true;
    }
    public void BeginGame() { if (this.Phase != RoomPhase.Starting) throw new InvalidOperationException("Room must be starting."); this.Phase = RoomPhase.InGame; }
    public void Drain() { if (this.Phase is RoomPhase.Ended or RoomPhase.Draining) return; this.Phase = RoomPhase.Draining; }
    public void Ended() => this.Phase = RoomPhase.Ended;
}

public enum RpcAuthority { ServerOnly, OwnerOnly, AnyAuthenticated }
public readonly record struct RpcEndpointCapability(string Endpoint, RpcAuthority Authority);
public readonly record struct RpcRequest(Guid RequestId, string Endpoint, long? ObjectId, string Caller, byte[] Payload, uint Tick);
public readonly record struct RpcResponse(Guid RequestId, bool Accepted, byte[]? Payload, string? Error = null);

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
