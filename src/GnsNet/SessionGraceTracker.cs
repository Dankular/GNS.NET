namespace GnsNet;

public sealed class SessionGraceTracker<TId> where TId : notnull
{
    private readonly TimeSpan gracePeriod;
    private readonly Dictionary<TId, DateTimeOffset> disconnected = new();
    private readonly object sync = new();
    public SessionGraceTracker(TimeSpan gracePeriod)
    {
        if (gracePeriod < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        this.gracePeriod = gracePeriod;
    }
    public void MarkDisconnected(TId id, DateTimeOffset? now = null) { lock (this.sync) this.disconnected[id] = (now ?? DateTimeOffset.UtcNow) + this.gracePeriod; }
    public bool TryResume(TId id, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (this.sync)
        {
            if (!this.disconnected.TryGetValue(id, out DateTimeOffset expires)) return false;
            this.disconnected.Remove(id);
            return timestamp <= expires;
        }
    }
    public void Expire(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (this.sync) foreach (var pair in this.disconnected.Where(x => x.Value < timestamp).ToArray()) this.disconnected.Remove(pair.Key);
    }
    public bool Contains(TId id) { lock (this.sync) return this.disconnected.ContainsKey(id); }
}
