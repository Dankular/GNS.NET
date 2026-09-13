namespace GnsNet;

public sealed class ClientPrediction<TInput, TState>
{
    private readonly Dictionary<uint, TInput> pending = new();
    public int PendingCount => this.pending.Count;
    public void Add(uint tick, TInput input) => this.pending[tick] = input;
    public TState Reconcile(uint acknowledgedTick, TState authoritative, Func<TState, TInput, TState> simulate)
    {
        TState state = authoritative;
        foreach ((uint tick, TInput input) in this.pending.Where(x => TickSequence.IsNewer(acknowledgedTick, x.Key)).OrderBy(x => unchecked(x.Key - acknowledgedTick)).ToArray())
        {
            state = simulate(state, input);
        }
        foreach (uint tick in this.pending.Keys.Where(tick => !TickSequence.IsNewer(acknowledgedTick, tick)).ToArray()) this.pending.Remove(tick);
        return state;
    }
}

public sealed class SnapshotInterpolator<TState>
{
    private (uint Tick, TState State)? previous;
    private (uint Tick, TState State)? latest;
    public void Add(uint tick, TState state)
    {
        if (this.latest is not null && !TickSequence.IsNewer(this.latest.Value.Tick, tick)) return;
        this.previous = this.latest;
        this.latest = (tick, state);
    }
    public bool TrySample(uint renderTick, Func<TState, TState, float, TState> interpolate, out TState state)
    {
        if (this.previous is null || this.latest is null) { state = default!; return false; }
        uint span = this.latest.Value.Tick - this.previous.Value.Tick;
        float amount = span == 0 ? 1f : Math.Clamp((float)(renderTick - this.previous.Value.Tick) / span, 0f, 1f);
        state = interpolate(this.previous.Value.State, this.latest.Value.State, amount);
        return true;
    }
}
