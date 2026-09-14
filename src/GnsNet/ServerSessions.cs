namespace GnsNet;

public sealed class ServerSessionRegistry<TId> where TId : notnull
{
    private readonly SessionGraceTracker<TId> grace;
    private readonly Dictionary<TId, GnsConnection> active = new();
    private readonly object sync = new();
    public ServerSessionRegistry(TimeSpan gracePeriod) => this.grace = new SessionGraceTracker<TId>(gracePeriod);
    public SessionRehydrationBuffer<TId> Rehydration { get; } = new();
    public IReadOnlyDictionary<TId, GnsConnection> Active { get { lock (this.sync) return new Dictionary<TId, GnsConnection>(this.active); } }
    public bool Attach(TId id, GnsConnection connection, DateTimeOffset? now = null)
    {
        // The return value reports resumption, not admission success.
        bool resumed = this.grace.TryResume(id, now);
        lock (this.sync) this.active[id] = connection;
        return resumed;
    }
    public void Detach(TId id, DateTimeOffset? now = null)
    {
        bool removed; lock (this.sync) removed = this.active.Remove(id); if (removed) this.grace.MarkDisconnected(id, now);
    }
    public bool Remove(TId id) { lock (this.sync) { bool removed = this.active.Remove(id); if (removed) this.Rehydration.Clear(id); return removed; } }
    public bool TryGet(TId id, out GnsConnection? connection) { lock (this.sync) return this.active.TryGetValue(id, out connection); }
    public void Expire(DateTimeOffset? now = null) => this.grace.Expire(now);
}
