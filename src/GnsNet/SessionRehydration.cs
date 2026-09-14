namespace GnsNet;

/// <summary>Bounded authoritative lifecycle journal used to rebuild a resumed session.</summary>
public sealed class SessionRehydrationBuffer<TSessionId> where TSessionId : notnull
{
    private readonly int capacity;
    private readonly Dictionary<TSessionId, Queue<NetworkObjectChange>> records = new();
    private readonly object sync = new();
    public SessionRehydrationBuffer(int capacity = 4096) { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
    public void Record(TSessionId session, NetworkObjectChange change)
    {
        lock (this.sync)
        {
            if (!this.records.TryGetValue(session, out Queue<NetworkObjectChange>? history)) this.records[session] = history = new();
            history.Enqueue(change); while (history.Count > this.capacity) history.Dequeue();
        }
    }
    public IReadOnlyList<NetworkObjectChange> Snapshot(TSessionId session) { lock (this.sync) return this.records.TryGetValue(session, out Queue<NetworkObjectChange>? history) ? history.ToArray() : []; }
    public int Replay(TSessionId session, NetworkObjectRegistry<string> clientRegistry)
    {
        ArgumentNullException.ThrowIfNull(clientRegistry); int applied = 0;
        foreach (NetworkObjectChange change in this.Snapshot(session)) if (clientRegistry.Apply(change)) applied++;
        return applied;
    }
    public void Clear(TSessionId session) { lock (this.sync) this.records.Remove(session); }
}
