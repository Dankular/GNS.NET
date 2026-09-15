namespace GnsNet;

using System.Diagnostics;

/// <summary>Provides monotonic timestamps for <see cref="NetworkClock"/>.</summary>
public interface INetworkTimeSource
{
    long Timestamp { get; }
    long Frequency { get; }
}

/// <summary>Production monotonic timestamp source backed by <see cref="Stopwatch"/>.</summary>
public sealed class StopwatchTimeSource : INetworkTimeSource
{
    public long Timestamp => Stopwatch.GetTimestamp();
    public long Frequency => Stopwatch.Frequency;
}

public enum NetworkClockState : byte { Unsynchronized, Acquiring, Synchronized, Holdover, Discontinuous }
public enum NetworkClockEvent : byte { Acquired, Holdover, Unsynchronized, HardResync, SampleRejected }

/// <summary>Four monotonic timestamps exchanged by the client and authoritative server.</summary>
public readonly record struct NetworkClockSample(uint Sequence, long ClientSentTimestamp, long ServerReceivedTimestamp,
    long ServerSentTimestamp, long ClientReceivedTimestamp, uint AuthoritativeServerTick, int ServerTickRateHz);

/// <summary>Validated operating limits for <see cref="NetworkClock"/>.</summary>
public sealed class NetworkClockOptions
{
    public int ServerTickRateHz { get; init; } = 30;
    public int AcquisitionSampleCount { get; init; } = 4;
    public int SampleWindowSize { get; init; } = 16;
    public TimeSpan MaximumAcceptedRtt { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumServerProcessingTime { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaximumAbsoluteOffset { get; init; } = TimeSpan.FromDays(1);
    public TimeSpan HoldoverTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public double OffsetSmoothing { get; init; } = .125;
    public double MaximumDriftPpm { get; init; } = 5_000;
    public double HardResyncThresholdTicks { get; init; } = 15;
    public int MaxCatchUpTicksPerFrame { get; init; } = 4;
    public TimeSpan MinimumInterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan BaseInterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan MaximumInterpolationDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    public double JitterMultiplier { get; init; } = 2;

    internal void Validate()
    {
        if (ServerTickRateHz is < 1 or > 1000 || AcquisitionSampleCount < 1 || SampleWindowSize < 1 || MaxCatchUpTicksPerFrame < 1)
            throw new ArgumentOutOfRangeException(nameof(ServerTickRateHz));
        if (MaximumAcceptedRtt <= TimeSpan.Zero || MaximumServerProcessingTime < TimeSpan.Zero || MaximumAbsoluteOffset <= TimeSpan.Zero || HoldoverTimeout <= TimeSpan.Zero ||
            MinimumInterpolationDelay < TimeSpan.Zero || BaseInterpolationDelay < MinimumInterpolationDelay || MaximumInterpolationDelay < BaseInterpolationDelay ||
            OffsetSmoothing is <= 0 or > 1 || MaximumDriftPpm < 0 || HardResyncThresholdTicks <= 0 || JitterMultiplier < 0)
            throw new ArgumentOutOfRangeException(nameof(NetworkClockOptions));
    }
}

/// <summary>An immutable view of synchronized simulation, prediction, and rendering time.</summary>
public readonly record struct NetworkClockSnapshot(NetworkClockState State, double EstimatedServerSeconds,
    double EstimatedServerTick, uint AuthoritativeServerTick, double PredictionTick, double RenderTick,
    TimeSpan Rtt, TimeSpan Jitter, TimeSpan Offset, double DriftPpm, TimeSpan InterpolationDelay, int BufferedSnapshots);

/// <summary>Clock counters suitable for application telemetry.</summary>
public sealed class NetworkClockMetrics
{
    public long AcceptedSamples { get; internal set; }
    public long RejectedSamples { get; internal set; }
    public long HardResyncs { get; internal set; }
    public long CatchUpTicks { get; internal set; }
    public long Holdovers { get; internal set; }
}

/// <summary>
/// Disciplines a local monotonic clock to the server's monotonic epoch. Timestamp values in a
/// sample must use the same monotonic-duration frequency as the supplied time source; wall-clock
/// timestamps are deliberately not accepted.
/// </summary>
public sealed class NetworkClock
{
    private readonly INetworkTimeSource source;
    private readonly NetworkClockOptions options;
    private readonly Queue<(double Offset, double Rtt)> samples = new();
    private bool hasSequence;
    private uint lastSequence;
    private bool hasOffset;
    private double offsetSeconds;
    private double driftPpm;
    private double previousOffset;
    private long previousOffsetTimestamp;
    private long lastSampleTimestamp;
    private uint serverTick;
    private long serverTickTimestamp;
    private int tickRate;
    private double lastRenderTick = double.NegativeInfinity;
    private TimeSpan rtt;
    private TimeSpan jitter;
    private TimeSpan interpolationDelay;

    public NetworkClock(INetworkTimeSource source, NetworkClockOptions? options = null)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        if (source.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(source));
        this.options = options ?? new NetworkClockOptions(); this.options.Validate();
        this.tickRate = this.options.ServerTickRateHz;
        this.interpolationDelay = this.options.BaseInterpolationDelay;
        this.Snapshot = new(NetworkClockState.Unsynchronized, 0, 0, 0, 0, 0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0, this.interpolationDelay, 0);
    }

    public NetworkClockState State { get; private set; } = NetworkClockState.Unsynchronized;
    public NetworkClockSnapshot Snapshot { get; private set; }
    public NetworkClockMetrics Metrics { get; } = new();
    public event Action<NetworkClockEvent>? Event;

    /// <summary>Adds one NTP-style timing response. Invalid, replayed, and extreme samples fail closed.</summary>
    public bool AddSample(in NetworkClockSample sample)
    {
        long now = this.source.Timestamp;
        if (sample.ServerTickRateHz is < 1 or > 1000 || (this.hasSequence && !TickSequence.IsNewer(this.lastSequence, sample.Sequence)))
            return this.Reject();
        double frequency = this.source.Frequency;
        double processing = (sample.ServerSentTimestamp - sample.ServerReceivedTimestamp) / frequency;
        double roundTrip = ((sample.ClientReceivedTimestamp - sample.ClientSentTimestamp) -
            (sample.ServerSentTimestamp - sample.ServerReceivedTimestamp)) / frequency;
        double offset = ((sample.ServerReceivedTimestamp - sample.ClientSentTimestamp) +
            (sample.ServerSentTimestamp - sample.ClientReceivedTimestamp)) / (2d * frequency);
        if (!double.IsFinite(processing) || !double.IsFinite(roundTrip) || !double.IsFinite(offset) || processing < 0 || roundTrip < 0 || Math.Abs(offset) > this.options.MaximumAbsoluteOffset.TotalSeconds ||
            processing > this.options.MaximumServerProcessingTime.TotalSeconds || roundTrip > this.options.MaximumAcceptedRtt.TotalSeconds)
            return this.Reject();

        if (this.hasOffset && this.samples.Count >= this.options.AcquisitionSampleCount &&
            Math.Abs(offset - this.offsetSeconds) > Math.Max(.25, this.jitter.TotalSeconds * 8 + .025))
            return this.Reject();

        this.hasSequence = true; this.lastSequence = sample.Sequence;
        this.samples.Enqueue((offset, roundTrip));
        while (this.samples.Count > this.options.SampleWindowSize) this.samples.Dequeue();
        (double Offset, double Rtt) best = this.samples.OrderBy(x => x.Rtt).First();
        if (!this.hasOffset)
        {
            this.offsetSeconds = best.Offset; this.hasOffset = true;
        }
        else
        {
            double error = best.Offset - this.offsetSeconds;
            this.offsetSeconds += error * this.options.OffsetSmoothing;
            double elapsed = (now - this.previousOffsetTimestamp) / frequency;
            if (elapsed > 0) this.driftPpm = Math.Clamp(this.driftPpm * .875 + ((this.offsetSeconds - this.previousOffset) / elapsed * 1_000_000d) * .125,
                -this.options.MaximumDriftPpm, this.options.MaximumDriftPpm);
        }
        this.previousOffset = this.offsetSeconds; this.previousOffsetTimestamp = now;
        this.lastSampleTimestamp = now;
        this.rtt = TimeSpan.FromSeconds(best.Rtt);
        this.jitter = TimeSpan.FromSeconds(this.jitter.TotalSeconds * .875 + Math.Abs(roundTrip - best.Rtt) * .125);
        this.interpolationDelay = ClampDelay(this.options.BaseInterpolationDelay + TimeSpan.FromTicks((long)(this.jitter.Ticks * this.options.JitterMultiplier)));

        bool tickRateChanged = this.tickRate != sample.ServerTickRateHz;
        double predictedAtSample = this.hasOffset ? this.EstimateServerTickAt(sample.ClientReceivedTimestamp) : sample.AuthoritativeServerTick;
        bool discontinuity = this.Metrics.AcceptedSamples > 0 && (tickRateChanged || Math.Abs(SignedTickDistance((uint)predictedAtSample, sample.AuthoritativeServerTick)) > this.options.HardResyncThresholdTicks);
        if (discontinuity)
        {
            this.State = NetworkClockState.Discontinuous; this.Metrics.HardResyncs++; this.Event?.Invoke(NetworkClockEvent.HardResync);
        }
        this.serverTick = sample.AuthoritativeServerTick;
        this.serverTickTimestamp = sample.ClientReceivedTimestamp;
        this.tickRate = sample.ServerTickRateHz;
        this.Metrics.AcceptedSamples++;
        if (discontinuity) { }
        else if (this.Metrics.AcceptedSamples >= this.options.AcquisitionSampleCount)
        {
            if (this.State != NetworkClockState.Synchronized) this.Event?.Invoke(NetworkClockEvent.Acquired);
            this.State = NetworkClockState.Synchronized;
        }
        else this.State = NetworkClockState.Acquiring;
        this.Update();
        return true;
    }

    /// <summary>Refreshes timelines at the current monotonic timestamp and transitions holdover state.</summary>
    public void Update(int bufferedSnapshots = 0)
    {
        long now = this.source.Timestamp;
        if (this.hasOffset)
        {
            double age = (now - this.lastSampleTimestamp) / (double)this.source.Frequency;
            if (age > this.options.HoldoverTimeout.TotalSeconds * 2)
            {
                if (this.State != NetworkClockState.Unsynchronized) this.Event?.Invoke(NetworkClockEvent.Unsynchronized);
                this.State = NetworkClockState.Unsynchronized;
            }
            else if (age > this.options.HoldoverTimeout.TotalSeconds && this.State == NetworkClockState.Synchronized)
            {
                this.State = NetworkClockState.Holdover; this.Metrics.Holdovers++; this.Event?.Invoke(NetworkClockEvent.Holdover);
            }
        }
        double estimated = this.hasOffset ? this.EstimateServerTickAt(now) : 0;
        double render = estimated - this.interpolationDelay.TotalSeconds * this.tickRate;
        if (double.IsFinite(this.lastRenderTick) && render < this.lastRenderTick && this.State != NetworkClockState.Discontinuous) render = this.lastRenderTick;
        this.lastRenderTick = render;
        this.Snapshot = new(this.State, this.hasOffset ? now / (double)this.source.Frequency + this.offsetSeconds : 0,
            estimated, this.serverTick, estimated, render, this.rtt, this.jitter, TimeSpan.FromSeconds(this.offsetSeconds), this.driftPpm,
            this.interpolationDelay, Math.Max(0, bufferedSnapshots));
    }

    /// <summary>Returns a bounded number of fixed simulation ticks needed to approach server time.</summary>
    public int TicksDue(uint localTick)
    {
        if (this.State is NetworkClockState.Unsynchronized or NetworkClockState.Acquiring) return 0;
        int behind = SignedTickDistance(localTick, (uint)Math.Floor(this.Snapshot.PredictionTick));
        int due = Math.Clamp(behind, 0, this.options.MaxCatchUpTicksPerFrame);
        this.Metrics.CatchUpTicks += due;
        return due;
    }

    private bool Reject() { this.Metrics.RejectedSamples++; this.Event?.Invoke(NetworkClockEvent.SampleRejected); return false; }
    private double EstimateServerTickAt(long localTimestamp)
    {
        double elapsed = (localTimestamp - this.serverTickTimestamp) / (double)this.source.Frequency;
        return this.serverTick + elapsed * this.tickRate * (1d + this.driftPpm / 1_000_000d);
    }
    private TimeSpan ClampDelay(TimeSpan delay) => delay < this.options.MinimumInterpolationDelay ? this.options.MinimumInterpolationDelay : delay > this.options.MaximumInterpolationDelay ? this.options.MaximumInterpolationDelay : delay;
    private static int SignedTickDistance(uint baseline, uint candidate) => unchecked((int)(candidate - baseline));
}
