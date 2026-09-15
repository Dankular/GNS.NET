namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class NetworkClockTests
{
    [Fact]
    public void Clock_AcquiresMonotonicTimelinesAndKeepsRenderTimeForwardOnly()
    {
        var source = new ManualTimeSource();
        var clock = new NetworkClock(source, new NetworkClockOptions
        {
            ServerTickRateHz = 20,
            AcquisitionSampleCount = 2,
            BaseInterpolationDelay = TimeSpan.FromMilliseconds(100),
            MinimumInterpolationDelay = TimeSpan.FromMilliseconds(50),
            MaximumInterpolationDelay = TimeSpan.FromMilliseconds(300)
        });

        source.AdvanceMilliseconds(100);
        clock.AddSample(new NetworkClockSample(1, 0, 50, 50, 100, 100, 20));
        source.AdvanceMilliseconds(10);
        source.AdvanceMilliseconds(100);
        clock.AddSample(new NetworkClockSample(2, 110, 160, 160, 210, 102, 20));
        Assert.Equal(NetworkClockState.Synchronized, clock.Snapshot.State);

        double firstRender = clock.Snapshot.RenderTick;
        source.AdvanceMilliseconds(100);
        clock.Update();
        Assert.True(clock.Snapshot.EstimatedServerTick > 102);
        Assert.True(clock.Snapshot.RenderTick >= firstRender);
        Assert.InRange(clock.TicksDue(99), 1, 4);
    }

    [Fact]
    public void Clock_RejectsReplayAndRttOutlierWithoutMovingEstimate()
    {
        var source = new ManualTimeSource();
        var clock = new NetworkClock(source, new NetworkClockOptions { AcquisitionSampleCount = 1, MaximumAcceptedRtt = TimeSpan.FromMilliseconds(250) });
        source.AdvanceMilliseconds(100);
        Assert.True(clock.AddSample(new NetworkClockSample(7, 0, 50, 50, 100, 3, 30)));
        TimeSpan offset = clock.Snapshot.Offset;
        Assert.False(clock.AddSample(new NetworkClockSample(7, 100, 150, 150, 200, 6, 30)));
        Assert.False(clock.AddSample(new NetworkClockSample(8, 100, 150, 150, 1000, 30, 30)));
        Assert.Equal(offset, clock.Snapshot.Offset);
        Assert.Equal(2, clock.Metrics.RejectedSamples);
    }

    [Fact]
    public void Clock_EntersHoldoverThenUnsynchronizedWhenSamplesStop()
    {
        var source = new ManualTimeSource();
        var clock = new NetworkClock(source, new NetworkClockOptions { AcquisitionSampleCount = 1, HoldoverTimeout = TimeSpan.FromSeconds(1) });
        clock.AddSample(new NetworkClockSample(1, 0, 10, 10, 20, 1, 30));
        source.AdvanceMilliseconds(1100); clock.Update();
        Assert.Equal(NetworkClockState.Holdover, clock.Snapshot.State);
        source.AdvanceMilliseconds(1100); clock.Update();
        Assert.Equal(NetworkClockState.Unsynchronized, clock.Snapshot.State);
    }

    [Fact]
    public void SnapshotBuffer_SamplesAtFractionalTickAcrossWraparound()
    {
        var snapshots = new SnapshotBuffer<float>();
        snapshots.Add(uint.MaxValue, 10); snapshots.Add(0, 20);
        Assert.True(snapshots.TrySample(uint.MaxValue + 0.5d, (from, to, alpha) => from + (to - from) * alpha, out float value));
        Assert.Equal(15, value);
    }

    private sealed class ManualTimeSource : INetworkTimeSource
    {
        public long Timestamp { get; private set; }
        public long Frequency => 1_000;
        public void AdvanceMilliseconds(long milliseconds) => this.Timestamp += milliseconds;
    }
}
