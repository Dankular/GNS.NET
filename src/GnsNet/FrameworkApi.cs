namespace GnsNet;

using GnsSharp;
using MemoryPack;

/// <summary>Options shared by the opinionated client/server framework facades.</summary>
public sealed class NetworkFrameworkOptions
{
    public byte InputOpcode { get; init; } = 1;
    public byte StateOpcode { get; init; } = 2;
    public int SnapshotBufferCapacity { get; init; } = 32;
    public uint InterpolationDelayTicks { get; init; } = 2;
    public void Validate()
    {
        if (InputOpcode is 0 or 0xFC or 0xFD or 0xFE || StateOpcode is 0 or 0xFC or 0xFD or 0xFE)
            throw new ArgumentException("Application opcodes overlap reserved framework control opcodes.");
        if (InputOpcode == StateOpcode) throw new ArgumentException("InputOpcode and StateOpcode must differ.");
        if (SnapshotBufferCapacity < 2) throw new ArgumentOutOfRangeException(nameof(SnapshotBufferCapacity));
    }
}

/// <summary>
/// Opinionated authoritative server facade. It wires serialized input, validation, simulation,
/// tick advancement, and state broadcast into one application-facing API.
/// </summary>
public sealed class GnsAuthoritativeServer<TSessionId, TState, TInput> where TSessionId : notnull where TState : IMemoryPackable<TState> where TInput : IMemoryPackable<TInput>
{
    private readonly NetworkFrameworkOptions options;
    public GnsServerHost<TSessionId> Host { get; }
    public AuthoritativeServer<TSessionId, TState, TInput> Simulation { get; }
    public ServerInputGuard<TSessionId, TInput> InputGuard { get; }
    public uint Tick => this.Simulation.Tick;
    public TState State => this.Simulation.State;

    public GnsAuthoritativeServer(GnsServer server, TState initialState, Func<TState, TSessionId, TInput, TState> applyInput,
        ServerInputGuard<TSessionId, TInput> inputGuard, TimeSpan sessionGracePeriod, ConnectionAdmission? admission = null,
        NetworkFrameworkOptions? options = null)
    {
        this.options = options ?? new NetworkFrameworkOptions();
        this.options.Validate();
        this.Host = new GnsServerHost<TSessionId>(server, sessionGracePeriod, admission);
        this.InputGuard = inputGuard ?? throw new ArgumentNullException(nameof(inputGuard));
        this.Simulation = new AuthoritativeServer<TSessionId, TState, TInput>(initialState, applyInput, inputGuard: inputGuard);
        this.Host.RegisterAuthoritativeInput(this.options.InputOpcode, inputGuard, this.Simulation);
    }

    public int Poll() => this.Host.Poll();
    public IReadOnlyDictionary<TSessionId, TState> Advance() => this.Simulation.Advance();
    public int PollAndAdvance() { int received = this.Poll(); this.Advance(); return received; }
    public void BroadcastState() => this.Host.Broadcast(this.options.StateOpcode, this.Tick, this.State, NetChannel.State.SendType());
    public EResult SendState(TSessionId session) => this.Host.Sessions.TryGet(session, out GnsConnection? connection) && connection is not null
        ? this.Host.Send(connection, this.options.StateOpcode, this.Tick, this.State, NetChannel.State.SendType())
        : EResult.InvalidParam;
    public EResult SendStateTo(TSessionId session) => this.Host.SendTo(session, this.options.StateOpcode, this.Tick, this.State, NetChannel.State.SendType());
}

/// <summary>
/// Opinionated client facade. It immediately predicts local input, receives authoritative states,
/// reconciles pending input, and exposes delayed interpolation for rendering remote state.
/// </summary>
public sealed class GnsPredictedClient<TInput, TState> where TInput : IMemoryPackable<TInput> where TState : IMemoryPackable<TState>
{
    private readonly Func<TState, TInput, TState> simulate;
    private readonly Func<TState, TState, float, TState> interpolate;
    private readonly NetworkFrameworkOptions options;
    public GnsClientHost Host { get; }
    public ClientPrediction<TInput, TState> Prediction { get; } = new();
    public SnapshotBuffer<TState> Snapshots { get; }
    public TState PredictedState { get; private set; }
    public TState? AuthoritativeState { get; private set; }
    public event Action<TState>? StateReconciled;

    public GnsPredictedClient(GnsClientHost host, TState initialState, Func<TState, TInput, TState> simulate,
        Func<TState, TState, float, TState> interpolate, NetworkFrameworkOptions? options = null)
    {
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        this.simulate = simulate ?? throw new ArgumentNullException(nameof(simulate));
        this.interpolate = interpolate ?? throw new ArgumentNullException(nameof(interpolate));
        this.options = options ?? new NetworkFrameworkOptions(); this.options.Validate();
        this.PredictedState = initialState;
        this.Snapshots = new SnapshotBuffer<TState>(this.options.SnapshotBufferCapacity);
        this.Host.Router.Register<TState>(this.options.StateOpcode, (state, frame) => this.ReceiveAuthoritative(frame.Tick, state));
    }

    public bool SubmitInput(uint tick, TInput input)
    {
        this.PredictedState = this.simulate(this.PredictedState, input);
        this.Prediction.Add(tick, input);
        return this.Host.Send(this.options.InputOpcode, tick, input, NetChannel.State.SendType());
    }

    public bool TryRender(uint serverTick, out TState state)
        => this.Snapshots.TrySample(serverTick > this.options.InterpolationDelayTicks ? serverTick - this.options.InterpolationDelayTicks : 0, this.interpolate, out state);

    private void ReceiveAuthoritative(uint tick, TState state)
    {
        this.AuthoritativeState = state;
        this.PredictedState = this.Prediction.Reconcile(tick, state, this.simulate);
        this.Snapshots.Add(tick, this.PredictedState);
        this.StateReconciled?.Invoke(this.PredictedState);
    }
}
