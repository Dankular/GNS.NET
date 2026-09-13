namespace GnsNet;

using System.Diagnostics;

/// <summary>
/// Drives a fixed-interval loop - a server simulation tick, or a client's periodic input send.
/// </summary>
/// <remarks>
/// This does not attempt catch-up: if a call to the tick action passed to <see cref="RunAsync"/>
/// runs long, the next tick simply starts late rather than firing extra ticks to compensate. That's
/// enough for a fixed low tick rate like the original POC's 20Hz; a project that needs strict
/// wall-clock-accurate simulation tick counts should build that on top.
/// </remarks>
public sealed class TickLoop
{
    public TickLoop(TimeSpan tickInterval)
    {
        this.TickInterval = tickInterval;
    }

    public TimeSpan TickInterval { get; }

    /// <summary>
    /// Runs <paramref name="onTick"/> once per <see cref="TickInterval"/> until
    /// <paramref name="cancellationToken"/> is canceled. <paramref name="onTick"/> receives the tick
    /// counter (starting at 0) and the actual elapsed time since the previous tick.
    /// </summary>
    public async Task RunAsync(Action<uint, TimeSpan> onTick, CancellationToken cancellationToken)
    {
        uint tick = 0;
        long lastTimestamp = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            TimeSpan elapsed = Stopwatch.GetElapsedTime(lastTimestamp, now);
            lastTimestamp = now;

            onTick(tick, elapsed);
            unchecked
            {
                tick++;
            }

            try
            {
                await Task.Delay(this.TickInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
