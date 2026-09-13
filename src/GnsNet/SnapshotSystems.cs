namespace GnsNet;

public readonly record struct Snapshot<T>(uint Tick, T State);

/// <summary>Buffers snapshots and renders a delayed, smooth view between two server ticks.</summary>
public sealed class SnapshotBuffer<T>
{
    private readonly int capacity;
    private readonly LinkedList<Snapshot<T>> snapshots = new();
    public SnapshotBuffer(int capacity = 32) { if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
    public int Count => this.snapshots.Count;
    public void Add(uint tick, T state)
    {
        if (this.snapshots.Last is not null && !TickSequence.IsNewer(this.snapshots.Last.Value.Tick, tick)) return;
        this.snapshots.AddLast(new Snapshot<T>(tick, state));
        while (this.snapshots.Count > this.capacity) this.snapshots.RemoveFirst();
    }
    public bool TrySample(uint renderTick, Func<T, T, float, T> interpolate, out T state)
    {
        var node = this.snapshots.First;
        while (node?.Next is not null && TickSequence.IsNewer(node.Next.Value.Tick, renderTick)) node = node.Next;
        if (node is null || node.Next is null) { state = default!; return false; }
        uint span = node.Next.Value.Tick - node.Value.Tick;
        float amount = span == 0 ? 1f : Math.Clamp((float)(renderTick - node.Value.Tick) / span, 0f, 1f);
        state = interpolate(node.Value.State, node.Next.Value.State, amount);
        return true;
    }
}

/// <summary>Creates compact application-defined deltas against per-client acknowledged baselines.</summary>
public sealed class DeltaCompressor<T>
{
    private readonly Func<T, T, T> createDelta;
    private readonly Func<T, T, T> applyDelta;
    private readonly Dictionary<object, T> baselines = new();
    public DeltaCompressor(Func<T, T, T> createDelta, Func<T, T, T> applyDelta) { this.createDelta = createDelta; this.applyDelta = applyDelta; }
    public T Create(object client, T current) { if (!this.baselines.TryGetValue(client, out T? baseline)) baseline = default!; return this.createDelta(baseline, current); }
    public T Apply(object client, T delta) { T baseline = this.baselines.TryGetValue(client, out T? value) ? value : default!; T state = this.applyDelta(baseline, delta); this.baselines[client] = state; return state; }
    public void Acknowledge(object client, T state) => this.baselines[client] = state;
    public void Remove(object client) => this.baselines.Remove(client);
}
