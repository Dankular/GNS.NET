namespace GnsNet;

public sealed class ClientPrediction<TInput, TState>
{
    private readonly Dictionary<uint, (TInput Input, PredictionTickMetadata Metadata)> pending = new();
    public int PendingCount => this.pending.Count;
    public int LastResimulatedTicks { get; private set; }
    public bool LastCorrected { get; private set; }
    public IReadOnlyList<PredictionTickMetadata> LastReplayedMetadata { get; private set; } = Array.Empty<PredictionTickMetadata>();
    public void Add(uint tick, TInput input) => this.Add(tick, input, PredictionTickMetadata.Default);
    public void Add(uint tick, TInput input, PredictionTickMetadata metadata) => this.pending[tick] = (input, metadata);
    public TState Reconcile(uint acknowledgedTick, TState authoritative, Func<TState, TInput, TState> simulate)
    {
        return this.Reconcile(acknowledgedTick, authoritative, (state, input, _) => simulate(state, input));
    }
    public TState Reconcile(uint acknowledgedTick, TState authoritative, Func<TState, TInput, PredictionTickMetadata, TState> simulate)
    {
        TState state = authoritative; int replayed = 0; var metadata = new List<PredictionTickMetadata>();
        foreach ((uint tick, (TInput Input, PredictionTickMetadata Metadata) value) in this.pending.Where(x => TickSequence.IsNewer(acknowledgedTick, x.Key)).OrderBy(x => unchecked(x.Key - acknowledgedTick)).ToArray())
        {
            state = simulate(state, value.Input, value.Metadata); metadata.Add(value.Metadata); replayed++;
        }
        foreach (uint tick in this.pending.Keys.Where(tick => !TickSequence.IsNewer(acknowledgedTick, tick)).ToArray()) this.pending.Remove(tick);
        this.LastReplayedMetadata = metadata; this.LastResimulatedTicks = replayed; this.LastCorrected = replayed != 0; return state;
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
