namespace GnsNet;

/// <summary>Runs a server-owned simulation. Clients submit inputs; only this class mutates truth.</summary>
public sealed class AuthoritativeServer<TClientId, TState, TInput> where TClientId : notnull
{
    private readonly Func<TState, TClientId, TInput, TState> applyInput;
    private readonly Func<TState, TState> cloneState;
    private readonly ServerInputGuard<TClientId, TInput>? inputGuard;
    private readonly Dictionary<TClientId, List<(uint Tick, TInput Input)>> inputs = new();
    private readonly Dictionary<TClientId, TState> clientViews = new();
    public AuthoritativeServer(TState initialState, Func<TState, TClientId, TInput, TState> applyInput, Func<TState, TState>? cloneState = null, ServerInputGuard<TClientId, TInput>? inputGuard = null)
    {
        this.State = initialState;
        this.applyInput = applyInput ?? throw new ArgumentNullException(nameof(applyInput));
        this.cloneState = cloneState ?? (state => state);
        this.inputGuard = inputGuard;
    }
    public uint Tick { get; private set; }
    public TState State { get; private set; }
    public int MaxPendingInputsPerClient { get; init; } = 256;
    public IReadOnlyDictionary<TClientId, TState> ClientViews => this.clientViews;
    public void AddClient(TClientId id) => this.inputs.TryAdd(id, new List<(uint, TInput)>());
    public void RemoveClient(TClientId id) { this.inputs.Remove(id); this.clientViews.Remove(id); }
    public void SubmitInput(TClientId id, uint tick, TInput input)
    {
        if (this.inputGuard is null || !this.inputs.TryGetValue(id, out List<(uint Tick, TInput Input)>? queue) || !this.inputGuard.TryAccept(id, input)) return;
        this.QueueInput(queue, tick, input);
    }
    internal void SubmitValidatedInput(TClientId id, uint tick, TInput input)
    {
        if (this.inputs.TryGetValue(id, out List<(uint Tick, TInput Input)>? queue)) this.QueueInput(queue, tick, input);
    }
    private void QueueInput(List<(uint Tick, TInput Input)> queue, uint tick, TInput input)
    {
        if (this.MaxPendingInputsPerClient <= 0 || queue.Count >= this.MaxPendingInputsPerClient) return;
        if (queue.Any(existing => existing.Tick == tick)) return;
        // Inputs for the current or already-simulated tick may arrive late and are
        // applied in chronological order. Inputs from the future remain invalid
        // until the simulation reaches that tick.
        if (!TickSequence.IsNewer(this.Tick, tick)) queue.Add((tick, input));
    }
    /// <summary>Applies queued inputs and advances truth by exactly one server tick.</summary>
    public IReadOnlyDictionary<TClientId, TState> Advance()
    {
        foreach (var client in this.inputs)
        {
            foreach (var input in client.Value.Where(input => !TickSequence.IsNewer(this.Tick, input.Tick)).OrderByDescending(input => unchecked(this.Tick - input.Tick)).ToArray())
            { client.Value.Remove(input); this.State = this.applyInput(this.State, client.Key, input.Input); }
            this.clientViews[client.Key] = this.cloneState(this.State);
        }
        unchecked { this.Tick++; }
        return this.clientViews;
    }
}
