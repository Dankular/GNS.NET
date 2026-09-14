namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class PredictionRuntimeTests
{
    [Fact]
    public void ClockSynchronizer_ComputesOffsetAndJitter()
    {
        var clock = new NetworkClockSynchronizer(); var origin = DateTimeOffset.UnixEpoch;
        clock.AddSample(origin, origin.AddMilliseconds(50), origin.AddMilliseconds(50), origin.AddMilliseconds(100));
        Assert.Equal(0, clock.Offset.TotalMilliseconds, 3); Assert.Equal(100, clock.LastRoundTrip.TotalMilliseconds, 3);
        clock.AddSample(origin.AddSeconds(1), origin.AddSeconds(1).AddMilliseconds(49), origin.AddSeconds(1).AddMilliseconds(49), origin.AddSeconds(1).AddMilliseconds(100));
        Assert.True(clock.Jitter >= TimeSpan.Zero); Assert.Equal(2, clock.Samples);
    }

    [Fact]
    public void Rollback_ReplaysOnlyNewerInputsWithinBoundedHistory()
    {
        var rollback = new RollbackBuffer<int, int>(); rollback.Record(1, 2, 2); rollback.Record(2, 3, 5); rollback.Record(3, 4, 9);
        Assert.Equal(13, rollback.Reconcile(1, 6, (state, input) => state + input)); Assert.Equal(2, rollback.Count);
    }

    [Fact]
    public void DirtyMaskQuantizationAndExtrapolationAreDeterministic()
    {
        var mask = new DirtyFieldMask(65); mask.Set(64); Assert.True(mask.IsSet(64)); mask.Clear(); Assert.False(mask.IsSet(64));
        QuantizedVector2 encoded = QuantizedVector2.Encode(0, 10, -100, 100); (float x, float y) = encoded.Decode(-100, 100);
        Assert.Equal(0, x, 1); Assert.Equal(10, y, 1); Assert.Equal(20, SnapshotExtrapolator.Linear(10, 15, 1, 2));
    }

    [Fact]
    public void TickRateCoordinator_ClampsCatchupAndDetectsAheadClient()
    {
        var coordinator = new TickRateCoordinator(60, 3); coordinator.ApplyServerClock(100, 30);
        Assert.Equal(3, coordinator.TicksToSimulate(90)); Assert.Equal(0, coordinator.TicksToSimulate(101)); Assert.True(coordinator.IsAhead(101));
        Assert.Equal(TimeSpan.FromSeconds(1d / 30), coordinator.TickDuration);
    }

    [Fact]
    public void RollbackDetailed_ReportsCorrectionsAndEnforcesResimulationBudget()
    {
        var rollback = new RollbackBuffer<int, int>(maxResimulationTicks: 2); rollback.Record(1, 1, 1); rollback.Record(2, 1, 2); rollback.Record(3, 1, 3);
        Assert.Throws<InvalidOperationException>(() => rollback.ReconcileDetailed(0, 0, (state, input) => state + input));
        var bounded = new RollbackBuffer<int, int>(maxResimulationTicks: 2); bounded.Record(1, 1, 1); bounded.Record(2, 1, 2);
        RollbackResult<int> result = bounded.ReconcileDetailed(1, 0, (state, input) => state + input);
        Assert.True(result.Corrected); Assert.Equal(1, result.ResimulatedTicks); Assert.Equal(1, bounded.CorrectionCount);
    }
}
