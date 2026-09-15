namespace GnsNet.Stride;

using global::Stride.Engine;

/// <summary>One-to-one network ID to Stride entity mapping with update-thread ownership checks.</summary>
public sealed class NetworkEntityViewRegistry : IDisposable
{
    private readonly Dictionary<ulong, NetworkEntityView> views = new();
    private int? updateThread;
    private bool disposed;

    /// <summary>Number of live presentation views.</summary>
    public int Count => this.views.Count;

    /// <summary>Binds mutation to the current Stride update thread.</summary>
    public void BindUpdateThread()
    {
        this.ThrowIfDisposed();
        this.updateThread ??= Environment.CurrentManagedThreadId;
        this.RequireUpdateThread();
    }

    /// <summary>Gets a view by network ID.</summary>
    public bool TryGet(ulong networkId, out NetworkEntityView view) => this.views.TryGetValue(networkId, out view!);

    /// <summary>Returns a stable point-in-time view snapshot for one update pass.</summary>
    public NetworkEntityView[] Snapshot() => this.views.Values.ToArray();

    /// <summary>Creates and registers a view. Duplicate live spawns are rejected.</summary>
    public bool TrySpawn(ulong networkId, ushort archetypeId, bool localPlayer, Func<Entity> factory, out NetworkEntityView? view)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.ThrowIfDisposed();
        this.RequireUpdateThread();
        if (networkId == 0 || this.views.ContainsKey(networkId)) { view = null; return false; }
        Entity entity = factory() ?? throw new InvalidOperationException("The view factory returned null.");
        view = new NetworkEntityView { NetworkId = networkId, ArchetypeId = archetypeId, IsLocalPlayer = localPlayer };
        entity.Add(view);
        this.views.Add(networkId, view);
        return true;
    }

    /// <summary>Despawns a view idempotently and detaches it from its scene.</summary>
    public bool Despawn(ulong networkId)
    {
        this.ThrowIfDisposed();
        this.RequireUpdateThread();
        if (!this.views.Remove(networkId, out NetworkEntityView? view)) return false;
        view.Entity.Scene = null;
        return true;
    }

    /// <summary>Removes every view, used on scene unload, reconnect, and system destruction.</summary>
    public void Clear()
    {
        this.ThrowIfDisposed();
        this.RequireUpdateThread();
        foreach (NetworkEntityView view in this.views.Values) view.Entity.Scene = null;
        this.views.Clear();
    }

    /// <summary>Releases the registry and all presentation mappings.</summary>
    public void Dispose()
    {
        if (this.disposed) return;
        this.RequireUpdateThread();
        this.Clear();
        this.disposed = true;
    }

    private void RequireUpdateThread()
    {
        if (this.updateThread is int expected && expected != Environment.CurrentManagedThreadId)
            throw new InvalidOperationException("Stride entity mutation must run on the bound Stride update thread.");
    }

    private void ThrowIfDisposed()
    {
        if (this.disposed) throw new ObjectDisposedException(nameof(NetworkEntityViewRegistry));
    }
}
