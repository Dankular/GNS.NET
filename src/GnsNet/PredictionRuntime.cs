namespace GnsNet;

/// <summary>Tracks server clock offset and jitter using NTP-style four-timestamp samples.</summary>
public sealed class NetworkClockSynchronizer
{
    private bool initialized;
    public TimeSpan Offset { get; private set; }
    public TimeSpan Jitter { get; private set; }
    public int Samples { get; private set; }
    public DateTimeOffset ToServerTime(DateTimeOffset clientTime) => clientTime + this.Offset;
    public void AddSample(DateTimeOffset clientSent, DateTimeOffset serverReceived, DateTimeOffset serverSent, DateTimeOffset clientReceived)
    {
        TimeSpan roundTrip = clientReceived - clientSent - (serverSent - serverReceived);
        TimeSpan offset = ((serverReceived - clientSent) + (serverSent - clientReceived)) / 2;
        if (!this.initialized) { this.Offset = offset; this.Jitter = TimeSpan.Zero; this.initialized = true; }
        else { this.Jitter = TimeSpan.FromTicks((long)(this.Jitter.Ticks * .9 + Math.Abs((offset - this.Offset).Ticks) * .1)); this.Offset = TimeSpan.FromTicks((long)(this.Offset.Ticks * .875 + offset.Ticks * .125)); }
        this.Samples++;
        LastRoundTrip = roundTrip;
    }
    public TimeSpan LastRoundTrip { get; private set; }
}

/// <summary>Coordinates client simulation ticks with the authoritative server clock.</summary>
public sealed class TickRateCoordinator
{
    public TickRateCoordinator(int serverHz, int maxCatchUpTicks = 4)
    { if (serverHz is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(serverHz)); if (maxCatchUpTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxCatchUpTicks)); ServerHz = serverHz; MaxCatchUpTicks = maxCatchUpTicks; }
    public int ServerHz { get; private set; }
    public int MaxCatchUpTicks { get; }
    public uint ServerTick { get; private set; }
    public TimeSpan TickDuration => TimeSpan.FromSeconds(1d / ServerHz);
    public void ApplyServerClock(uint serverTick, int serverHz)
    { if (serverHz is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(serverHz)); ServerTick = serverTick; ServerHz = serverHz; }
    public int TicksToSimulate(uint localTick)
    { uint behind = unchecked(ServerTick - localTick); return behind > 0x7FFFFFFF ? 0 : Math.Min((int)behind, MaxCatchUpTicks); }
    public bool IsAhead(uint localTick) => TickSequence.IsNewer(ServerTick, localTick);
}

/// <summary>Bounded tick history used to rewind an authoritative state and replay unacknowledged inputs.</summary>
public sealed class RollbackBuffer<TInput, TState>
{
    private readonly int capacity;
    private readonly int maxResimulationTicks;
    private readonly SortedDictionary<uint, (TInput Input, TState State)> entries = new();
    public RollbackBuffer(int capacity = 256, int maxResimulationTicks = 128) { if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity)); if (maxResimulationTicks < 1) throw new ArgumentOutOfRangeException(nameof(maxResimulationTicks)); this.capacity = capacity; this.maxResimulationTicks = maxResimulationTicks; }
    public int Count => this.entries.Count;
    public long CorrectionCount { get; private set; }
    public long ResimulatedTickCount { get; private set; }
    public void Record(uint tick, TInput input, TState predictedState)
    {
        this.entries[tick] = (input, predictedState);
        while (this.entries.Count > this.capacity) this.entries.Remove(this.entries.Keys.First());
    }
    public TState Reconcile(uint authoritativeTick, TState authoritativeState, Func<TState, TInput, TState> simulate)
    {
        return this.ReconcileDetailed(authoritativeTick, authoritativeState, simulate).State;
    }
    public RollbackResult<TState> ReconcileDetailed(uint authoritativeTick, TState authoritativeState, Func<TState, TInput, TState> simulate)
    {
        ArgumentNullException.ThrowIfNull(simulate); TState state = authoritativeState; int replayed = 0;
        foreach ((uint tick, (TInput Input, TState State) value) in this.entries.Where(x => TickSequence.IsNewer(authoritativeTick, x.Key)).OrderBy(x => unchecked(x.Key - authoritativeTick)))
        { if (replayed++ >= this.maxResimulationTicks) throw new InvalidOperationException("Rollback exceeds the configured resimulation budget."); state = simulate(state, value.Input); }
        if (replayed != 0) this.CorrectionCount++; this.ResimulatedTickCount += replayed;
        foreach (uint tick in this.entries.Keys.Where(x => !TickSequence.IsNewer(authoritativeTick, x)).ToArray()) this.entries.Remove(tick);
        return new RollbackResult<TState>(state, replayed != 0, replayed);
    }
}

public readonly record struct RollbackResult<TState>(TState State, bool Corrected, int ResimulatedTicks);

/// <summary>Compact dirty-field bitset for generated or hand-written replicated state encoders.</summary>
public sealed class DirtyFieldMask
{
    private readonly ulong[] words;
    private readonly int fieldCount;
    public DirtyFieldMask(int fieldCount) { if (fieldCount < 1) throw new ArgumentOutOfRangeException(nameof(fieldCount)); this.fieldCount = fieldCount; this.words = new ulong[(fieldCount + 63) / 64]; }
    public void Set(int field) { this.Check(field); this.words[field / 64] |= 1UL << (field % 64); }
    public bool IsSet(int field) { this.Check(field); return (this.words[field / 64] & (1UL << (field % 64))) != 0; }
    public void Clear() => Array.Clear(this.words);
    public ReadOnlySpan<ulong> Words => this.words;
    private void Check(int field) { if ((uint)field >= this.fieldCount) throw new ArgumentOutOfRangeException(nameof(field)); }
}

public readonly record struct QuantizedVector2(ushort X, ushort Y)
{
    public static QuantizedVector2 Encode(float x, float y, float min, float max)
    {
        if (!(max > min) || float.IsNaN(x) || float.IsNaN(y)) throw new ArgumentOutOfRangeException(nameof(max));
        static ushort Quantize(float value, float minValue, float maxValue) => (ushort)Math.Clamp(MathF.Round((value - minValue) / (maxValue - minValue) * ushort.MaxValue), 0, ushort.MaxValue);
        return new(Quantize(x, min, max), Quantize(y, min, max));
    }
    public (float X, float Y) Decode(float min, float max) => (min + this.X / (float)ushort.MaxValue * (max - min), min + this.Y / (float)ushort.MaxValue * (max - min));
}

/// <summary>Bounded extrapolation from the last two snapshots.</summary>
public static class SnapshotExtrapolator
{
    public static float Linear(float previous, float latest, uint elapsedTicks, uint maxTicks)
    {
        if (maxTicks == 0) return latest;
        return latest + (latest - previous) * Math.Clamp(elapsedTicks, 0, maxTicks);
    }
}
