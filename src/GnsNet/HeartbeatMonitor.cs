namespace GnsNet;

public enum HeartbeatStatus { Alive, TimedOut }

/// <summary>Game-level liveness monitor, independent of GNS transport keepalive.</summary>
public sealed class HeartbeatMonitor<TId> where TId : notnull
{
    private readonly TimeSpan timeout;
    private readonly Dictionary<TId, DateTimeOffset> lastSeen = new();
    private readonly object sync = new();
    public HeartbeatMonitor(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        this.timeout = timeout;
    }
    public event Action<TId>? TimedOut;
    public void Touch(TId id, DateTimeOffset? now = null) { lock (this.sync) this.lastSeen[id] = now ?? DateTimeOffset.UtcNow; }
    public HeartbeatStatus GetStatus(TId id, DateTimeOffset? now = null)
    { lock (this.sync) return this.lastSeen.TryGetValue(id, out var seen) && (now ?? DateTimeOffset.UtcNow) - seen <= this.timeout ? HeartbeatStatus.Alive : HeartbeatStatus.TimedOut; }
    public void Poll(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        TId[] expired; lock (this.sync) { expired = this.lastSeen.Where(x => timestamp - x.Value > this.timeout).Select(x => x.Key).ToArray(); foreach (TId id in expired) this.lastSeen.Remove(id); }
        foreach (TId id in expired) this.TimedOut?.Invoke(id);
    }
    public void Remove(TId id) { lock (this.sync) this.lastSeen.Remove(id); }
}
