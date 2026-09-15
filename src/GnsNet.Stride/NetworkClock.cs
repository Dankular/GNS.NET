namespace GnsNet.Stride;

using System.Diagnostics;

/// <summary>Monotonic timestamp source used by the adapter clock.</summary>
public interface INetworkTimeSource
{
    /// <summary>Current monotonic timestamp.</summary>
    long Timestamp { get; }

    /// <summary>Timestamp frequency per second.</summary>
    long Frequency { get; }
}

/// <summary>Production monotonic timestamp source backed by Stopwatch.</summary>
public sealed class StopwatchTimeSource : INetworkTimeSource
{
    /// <inheritdoc />
    public long Timestamp => Stopwatch.GetTimestamp();

    /// <inheritdoc />
    public long Frequency => Stopwatch.Frequency;
}

/// <summary>Clock state exposed to presentation and diagnostics.</summary>
public enum NetworkClockState : byte { Acquiring, Tracking, Holdover, HardResync }

/// <summary>NTP-style monotonic sample; timestamps must use the same units on each endpoint.</summary>
public readonly record struct NetworkClockSample(long ClientSend, long ServerReceive, long ServerSend, long ClientReceive)
{
    /// <summary>Calculates the NTP round-trip duration.</summary>
    public long RoundTrip => checked((ClientReceive - ClientSend) - (ServerSend - ServerReceive));

    /// <summary>Calculates server-minus-client offset.</summary>
    public long Offset => checked(((ServerReceive - ClientSend) + (ServerSend - ClientReceive)) / 2);
}

/// <summary>Immutable server, prediction, and render timeline values.</summary>
public readonly record struct NetworkClockSnapshot(
    double ServerTimeSeconds,
    double PredictionTimeSeconds,
    double RenderTimeSeconds,
    double RenderTick,
    uint ServerTick,
    float InterpolationDelayTicks,
    NetworkClockState State,
    TimeSpan RoundTrip,
    TimeSpan Jitter,
    double OffsetMilliseconds,
    double DriftPartsPerMillion,
    long CatchUpTicks,
    long HardResyncs);

/// <summary>Clock policy used by <see cref="NetworkClock"/>.</summary>
public sealed class NetworkClockOptions
{
    /// <summary>Authoritative tick rate.</summary>
    public int ServerTickRate { get; init; } = 30;
    /// <summary>Initial render delay in ticks.</summary>
    public float InitialInterpolationDelayTicks { get; init; } = 2;
    /// <summary>Minimum render delay.</summary>
    public float MinimumInterpolationDelayTicks { get; init; } = 1;
    /// <summary>Maximum render delay.</summary>
    public float MaximumInterpolationDelayTicks { get; init; } = 8;
    /// <summary>Maximum ordinary correction rate in seconds per update.</summary>
    public double MaximumSlewSecondsPerUpdate { get; init; } = 0.025;
    /// <summary>Maximum permitted sample RTT.</summary>
    public TimeSpan MaximumRoundTrip { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Maximum bounded prediction lead in ticks.</summary>
    public int MaximumPredictionLeadTicks { get; init; } = 2;
}

/// <summary>Drift-aware monotonic clock for adapter presentation and local prediction.</summary>
public sealed class NetworkClock
{
    private readonly INetworkTimeSource timeSource;
    private readonly NetworkClockOptions options;
    private readonly long startTimestamp;
    private bool hasSample;
    private double offsetSeconds;
    private double targetOffsetSeconds;
    private double driftPpm;
    private long lastSampleTimestamp;
    private double lastSampleOffset;
    private double serverTime;
    private double renderTime;
    private uint serverTick;
    private long lastUpdateTimestamp;
    private float interpolationDelay;
    private TimeSpan roundTrip;
    private TimeSpan jitter;
    private double lastRtt;

    /// <summary>Creates a clock using a monotonic source.</summary>
    public NetworkClock(INetworkTimeSource? timeSource = null, NetworkClockOptions? options = null)
    {
        this.timeSource = timeSource ?? new StopwatchTimeSource();
        this.options = options ?? new NetworkClockOptions();
        if (this.timeSource.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(timeSource));
        if (this.options.ServerTickRate is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(options));
        if (this.options.MinimumInterpolationDelayTicks < 0 || this.options.MaximumInterpolationDelayTicks < this.options.MinimumInterpolationDelayTicks)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.interpolationDelay = Math.Clamp(this.options.InitialInterpolationDelayTicks, this.options.MinimumInterpolationDelayTicks, this.options.MaximumInterpolationDelayTicks);
        this.startTimestamp = this.lastUpdateTimestamp = this.timeSource.Timestamp;
    }

    /// <summary>Current clock snapshot.</summary>
    public NetworkClockSnapshot Snapshot => new(this.serverTime, this.serverTime + this.options.MaximumPredictionLeadTicks / (double)this.options.ServerTickRate,
        this.renderTime, this.renderTime * this.options.ServerTickRate, this.serverTick, this.interpolationDelay,
        this.hasSample ? NetworkClockState.Tracking : NetworkClockState.Acquiring, this.roundTrip, this.jitter,
        this.offsetSeconds * 1000, this.driftPpm, 0, 0);

    /// <summary>Adds an NTP sample and rejects implausible or negative RTT samples.</summary>
    public bool AddSample(NetworkClockSample sample)
    {
        long rtt = sample.RoundTrip;
        if (rtt < 0 || rtt > checked((long)(this.options.MaximumRoundTrip.TotalSeconds * this.timeSource.Frequency))) return false;
        double offset = sample.Offset / (double)this.timeSource.Frequency;
        if (double.IsNaN(offset) || double.IsInfinity(offset)) return false;
        double rttSeconds = rtt / (double)this.timeSource.Frequency;
        this.roundTrip = TimeSpan.FromSeconds(rttSeconds);
        this.jitter = TimeSpan.FromSeconds(this.hasSample ? this.jitter.TotalSeconds * .9 + Math.Abs(rttSeconds - this.lastRtt) * .1 : 0);
        if (this.hasSample && sample.ClientSend > this.lastSampleTimestamp)
        {
            double elapsed = (sample.ClientSend - this.lastSampleTimestamp) / (double)this.timeSource.Frequency;
            this.driftPpm = Math.Clamp(this.driftPpm * .9 + ((offset - this.lastSampleOffset) / elapsed) * 1_000_000 * .1, -5000, 5000);
        }
        this.lastSampleTimestamp = sample.ClientSend;
        this.lastSampleOffset = offset;
        this.lastRtt = rttSeconds;
        this.targetOffsetSeconds = offset;
        if (!this.hasSample) { this.offsetSeconds = offset; this.hasSample = true; }
        this.interpolationDelay = Math.Clamp(this.interpolationDelay + (float)Math.Clamp(rttSeconds * this.options.ServerTickRate / 2 - this.interpolationDelay, -0.25, 0.5), this.options.MinimumInterpolationDelayTicks, this.options.MaximumInterpolationDelayTicks);
        return true;
    }

    /// <summary>Advances timelines from a server tick using only monotonic elapsed time.</summary>
    public void Update(uint authoritativeServerTick)
    {
        long now = this.timeSource.Timestamp;
        double elapsed = Math.Max(0, (now - this.lastUpdateTimestamp) / (double)this.timeSource.Frequency);
        this.lastUpdateTimestamp = now;
        this.serverTick = authoritativeServerTick;
        this.serverTime = authoritativeServerTick / (double)this.options.ServerTickRate;
        if (this.hasSample)
        {
            double correction = this.targetOffsetSeconds - this.offsetSeconds;
            this.offsetSeconds += Math.Clamp(correction, -this.options.MaximumSlewSecondsPerUpdate, this.options.MaximumSlewSecondsPerUpdate);
        }
        double targetRender = this.serverTime - this.interpolationDelay / this.options.ServerTickRate;
        if (targetRender < this.renderTime) targetRender = this.renderTime;
        this.renderTime += Math.Min(targetRender - this.renderTime, Math.Max(elapsed * 2, 0.001));
    }

    /// <summary>Forces a reconnect/epoch reset and permits initial acquisition again.</summary>
    public void Reset()
    {
        this.hasSample = false;
        this.offsetSeconds = this.targetOffsetSeconds = 0;
        this.renderTime = this.serverTime = 0;
        this.interpolationDelay = Math.Clamp(this.options.InitialInterpolationDelayTicks, this.options.MinimumInterpolationDelayTicks, this.options.MaximumInterpolationDelayTicks);
    }
}
