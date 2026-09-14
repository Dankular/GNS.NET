namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class PredictionRuntimeTests
{
    [Fact]
    public async Task PredictedClient_FacadePropagatesClockDriftAndCorrectionMetrics()
    {
        var transport = new ReconnectableClient(() => throw new InvalidOperationException());
        var host = new GnsClientHost(transport);
        var options = new NetworkFrameworkOptions { ServerTickRateHz = 30, MaxPredictionCatchUpTicks = 3 };
        var client = new GnsPredictedClient<TestState, TestState>(host, new TestState { Value = 0 },
            (state, input) => new TestState { Value = state.Value + input.Value },
            (from, to, amount) => new TestState { Value = from.Value + (int)MathF.Round((to.Value - from.Value) * amount) }, options,
            (predicted, authoritative) => Math.Abs(predicted.Value - authoritative.Value));
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        client.ApplyServerTiming(10, 30, origin, origin.AddMilliseconds(50), origin.AddMilliseconds(50), origin.AddMilliseconds(100));
        client.ApplyServerTiming(11, 30, origin.AddSeconds(1), origin.AddSeconds(1).AddMilliseconds(51), origin.AddSeconds(1).AddMilliseconds(51), origin.AddSeconds(1).AddMilliseconds(100));
        Assert.Equal(11u, client.ServerTick);
        Assert.Equal(3, client.TicksToSimulate(7));
        Assert.Equal(client.Clock.DriftPartsPerMillion, client.TickCoordinator.AppliedDriftPartsPerMillion);
        Assert.NotEqual(TimeSpan.FromSeconds(1d / 30), client.TickDuration);

        client.SubmitInput(12, new TestState { Value = 5 });
        host.Router.Dispatch(new NetFrame(options.StateOpcode, 12, NetSerializer.Serialize(new TestState { Value = 3 })));
        Assert.Equal(2d, client.LastMispredictionMagnitude);
        Assert.Equal(1, client.MispredictionCount);
        Assert.Equal(2d, client.TotalMispredictionMagnitude);
        await host.DisposeAsync();
    }

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
    public void DirtyFieldMaskCodec_RoundTripsOnlyDirtyFieldsAndRejectsSchemaMismatch()
    {
        var mask = new DirtyFieldMask(70); mask.Set(1); mask.Set(65);
        byte[] encoded = DirtyFieldMaskCodec.Encode(3, mask, field => [(byte)field, 7]);
        var decoded = DirtyFieldMaskCodec.Decode(encoded, 3, 70);
        Assert.True(decoded.Mask.IsSet(1)); Assert.True(decoded.Mask.IsSet(65)); Assert.False(decoded.Mask.IsSet(2));
        Assert.Equal(new byte[] { 1, 7 }, decoded.Fields[1]); Assert.Equal(new byte[] { 65, 7 }, decoded.Fields[65]);
        Assert.Throws<InvalidDataException>(() => DirtyFieldMaskCodec.Decode(encoded, 4, 70));
    }

    [Fact]
    public void DirtyFieldMaskCodec_RejectsDeterministicMalformedCorpus()
    {
        var mask = new DirtyFieldMask(3); mask.Set(2); byte[] valid = DirtyFieldMaskCodec.Encode(3, mask, _ => [9]);
        for (int length = 0; length < valid.Length; length++) Assert.Throws<InvalidDataException>(() => DirtyFieldMaskCodec.Decode(valid.AsSpan(0, length), 3, 3));
        var random = new Random(17);
        for (int i = 0; i < 256; i++)
        {
            byte[] bytes = new byte[random.Next(0, 96)]; random.NextBytes(bytes);
            try { DirtyFieldMaskCodec.Decode(bytes, 3, 3); } catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void Rollback_ReplaysOnlyNewerInputsWithinBoundedHistory()
    {
        var rollback = new RollbackBuffer<int, int>(); rollback.Record(1, 2, 2); rollback.Record(2, 3, 5); rollback.Record(3, 4, 9);
        Assert.Equal(13, rollback.Reconcile(1, 6, (state, input) => state + input)); Assert.Equal(2, rollback.Count);
    }

    [Fact]
    public void ClientPrediction_ReportsReconciliationCostAndCorrection()
    {
        var prediction = new ClientPrediction<int, int>(); prediction.Add(2, 3); prediction.Add(3, 4);
        Assert.Equal(7, prediction.Reconcile(1, 0, (state, input) => state + input));
        Assert.True(prediction.LastCorrected); Assert.Equal(2, prediction.LastResimulatedTicks); Assert.Equal(2, prediction.PendingCount);
        prediction.Reconcile(3, 10, (state, input) => state + input);
        Assert.False(prediction.LastCorrected); Assert.Equal(0, prediction.LastResimulatedTicks); Assert.Equal(0, prediction.PendingCount);
    }

    [Fact]
    public void SnapshotInterpolator_IgnoresDuplicateAndOldSnapshots()
    {
        var interpolator = new SnapshotInterpolator<int>(); interpolator.Add(1, 10); interpolator.Add(2, 20); interpolator.Add(2, 99); interpolator.Add(1, 1);
        Assert.True(interpolator.TrySample(2, (a, b, amount) => (int)(a + (b - a) * amount), out int value)); Assert.Equal(20, value);
    }

    [Fact]
    public void RollbackBuffer_ReconcilesLongWindowWithinConfiguredBudget()
    {
        var rollback = new RollbackBuffer<int, int>(capacity: 32, maxResimulationTicks: 32);
        for (uint tick = 1; tick <= 20; tick++) rollback.Record(tick, 1, (int)tick);
        RollbackResult<int> result = rollback.ReconcileDetailed(1, 0, (state, input) => state + input);
        Assert.True(result.Corrected); Assert.Equal(19, result.ResimulatedTicks); Assert.Equal(19, rollback.ResimulatedTickCount);
    }

    [Fact]
    public void PredictedWorldRuntime_PredictsReconcilesAndSmoothsRenderedState()
    {
        var runtime = new PredictedWorldRuntime<int, float>(0, (state, input) => state + input, (from, to, amount) => from + (to - from) * amount);
        runtime.Predict(1, 2); runtime.Predict(2, 2); RollbackResult<float> correction = runtime.Reconcile(1, 1, 4);
        Assert.True(correction.Corrected); Assert.Equal(3, runtime.PredictedState); Assert.Equal(3.75f, runtime.RenderedState, 3); Assert.Equal(1, correction.ResimulatedTicks);
        Assert.True(runtime.StepRenderedCorrection() < 3.75f);
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
    public void TickRateCoordinator_AppliesBoundedMeasuredClockDrift()
    {
        var coordinator = new TickRateCoordinator(60); coordinator.ApplyMeasuredDrift(2500);
        Assert.Equal(2500, coordinator.AppliedDriftPartsPerMillion); Assert.True(coordinator.TickDuration < TimeSpan.FromSeconds(1d / 60));
        coordinator.ApplyMeasuredDrift(100_000); Assert.Equal(5000, coordinator.AppliedDriftPartsPerMillion);
        coordinator.ApplyMeasuredDrift(-100_000); Assert.Equal(-5000, coordinator.AppliedDriftPartsPerMillion);
    }

    [Fact]
    public async Task TickLoop_UsesBoundedCoordinatorCatchup()
    {
        var coordinator = new TickRateCoordinator(60, 3); coordinator.ApplyServerClock(10, 60); using var cancellation = new CancellationTokenSource(); var ticks = new List<uint>();
        await new TickLoop(TimeSpan.Zero).RunCoordinatedAsync((tick, _) => { ticks.Add(tick); if (ticks.Count >= 3) cancellation.Cancel(); }, coordinator, cancellation.Token);
        Assert.Equal(new uint[] { 0, 1, 2 }, ticks);
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

    [Fact]
    public void ClockAndCorrectionSmoother_TrackDriftAndConverge()
    {
        var clock = new NetworkClockSynchronizer(); DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        clock.AddSample(origin, origin.AddMilliseconds(50), origin.AddMilliseconds(50), origin.AddMilliseconds(100)); clock.AddSample(origin.AddSeconds(1), origin.AddSeconds(1).AddMilliseconds(51), origin.AddSeconds(1).AddMilliseconds(51), origin.AddSeconds(1).AddMilliseconds(100));
        Assert.NotEqual(0, clock.DriftPartsPerMillion);
        var smoother = new CorrectionSmoother(0); smoother.Correct(12, 3); Assert.Equal(4, smoother.Step()); Assert.Equal(8, smoother.Step()); Assert.Equal(12, smoother.Step());
    }
}
