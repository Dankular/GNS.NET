namespace GnsNet.Tests;

using GnsNet;
using Xunit;
using System.Buffers.Binary;

public sealed class PredictionRuntimeTests
{
    [Fact]
    public async Task PredictedClient_FacadePropagatesClockDriftAndCorrectionMetrics()
    {
        var transport = new ReconnectableClient(() => throw new InvalidOperationException());
        var host = new GnsClientHost(transport);
        var options = new NetworkFrameworkOptions { ServerTickRateHz = 30, MaxPredictionCatchUpTicks = 3,
            PredictionTimestep = TimeSpan.FromMilliseconds(16), DeterministicSeed = 42, WorldVersion = 7 };
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
        Assert.Equal(new PredictionTickMetadata(TimeSpan.FromMilliseconds(16), 42, 7), client.PredictionMetadata);

        var replayMetadata = new PredictionTickMetadata(TimeSpan.FromMilliseconds(20), 99, 8);
        client.SubmitInput(13, new TestState { Value = 5 }, replayMetadata);
        host.Router.Dispatch(new NetFrame(options.StateOpcode, 12, NetSerializer.Serialize(new TestState { Value = 3 })));
        Assert.Equal(2d, client.LastMispredictionMagnitude);
        Assert.Equal(1, client.MispredictionCount);
        Assert.Equal(2d, client.TotalMispredictionMagnitude);
        Assert.Equal(new[] { replayMetadata }, client.LastReplayedMetadata);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task PredictedClient_ReconcilesInputsAcrossTickWraparound()
    {
        var transport = new ReconnectableClient(() => throw new InvalidOperationException());
        var host = new GnsClientHost(transport);
        var client = new GnsPredictedClient<TestState, TestState>(host, new TestState { Value = 0 },
            (state, input) => new TestState { Value = state.Value + input.Value },
            (from, to, amount) => new TestState { Value = amount < 1f ? from.Value : to.Value });

        client.SubmitInput(uint.MaxValue, new TestState { Value = 1 });
        client.SubmitInput(0, new TestState { Value = 1 });
        host.Router.Dispatch(new NetFrame(2, uint.MaxValue - 1, NetSerializer.Serialize(new TestState { Value = 0 })));

        Assert.Equal(2, client.PredictedState.Value);
        Assert.Equal(2, client.LastResimulatedTicks);
        Assert.True(client.LastCorrected);
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
    public void ReplicationMalformedCorpus_RejectsBoundedlyAcrossSchemaWrapPartialAndBaselineCases()
    {
        var mask = new DirtyFieldMask(3); mask.Set(1);
        byte[] valid = DirtyFieldMaskCodec.Encode(7, mask, _ => [0xA5, 0x5A]);
        var schemaMismatch = valid.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(schemaMismatch, 3);
        var partialEntity = valid[..^1];
        foreach (byte[] payload in new[] { schemaMismatch, partialEntity, Array.Empty<byte>(), new byte[4096] })
        {
            Assert.Throws<InvalidDataException>(() => DirtyFieldMaskCodec.Decode(payload, 7, 3, maximumFieldBytes: 128));
        }

        var snapshots = new SnapshotBuffer<int>(8);
        snapshots.Add(uint.MaxValue - 1, 1); snapshots.Add(uint.MaxValue, 2); snapshots.Add(0, 3); snapshots.Add(1, 4);
        snapshots.Add(uint.MaxValue - 2, 99);
        Assert.Equal(4, snapshots.Count);

        var delta = new DeltaCompressor<int>((baseline, current) => current - baseline, (baseline, change) => baseline + change);
        Assert.Equal(5, delta.Apply("baseline-lost", 5));
        delta.Acknowledge("baseline-lost", 5);
        Assert.Equal(7, delta.Apply("baseline-lost", 2));
    }

    [Fact]
    public void DirtyFieldMaskCodec_PropertyCorpusIsBoundedAndNeverLeaksUnexpectedParserExceptions()
    {
        var random = new Random(0x5EED);
        const int fieldCount = 129;
        const int schema = 11;
        var validMask = new DirtyFieldMask(fieldCount);
        validMask.Set(0); validMask.Set(64); validMask.Set(128);
        byte[] valid = DirtyFieldMaskCodec.Encode(schema, validMask, field => [(byte)field, (byte)(field ^ 0xA5)]);

        for (int iteration = 0; iteration < 2_000; iteration++)
        {
            byte[] candidate = new byte[random.Next(0, valid.Length + 32)];
            random.NextBytes(candidate);
            if (iteration % 4 == 0)
            {
                candidate = valid.ToArray();
                int mutations = 1 + random.Next(8);
                for (int mutation = 0; mutation < mutations; mutation++) candidate[random.Next(candidate.Length)] ^= (byte)(1 << random.Next(8));
            }
            else if (iteration % 4 == 1)
            {
                candidate = valid[..random.Next(valid.Length)].ToArray();
            }

            try
            {
                (int decodedSchema, DirtyFieldMask mask, IReadOnlyDictionary<int, byte[]> fields) = DirtyFieldMaskCodec.Decode(candidate, schema, fieldCount, maximumFieldBytes: 64);
                Assert.Equal(schema, decodedSchema);
                Assert.All(fields, field =>
                {
                    Assert.InRange(field.Key, 0, fieldCount - 1);
                    Assert.True(mask.IsSet(field.Key));
                    Assert.InRange(field.Value.Length, 0, 64);
                });
            }
            catch (InvalidDataException)
            {
                // Malformed candidates are expected to fail closed.
            }
        }
    }

    [Fact]
    public void DirtyFieldMaskCodec_SeededFuzzRunsAcrossIndependentInputFamilies()
    {
        const int fieldCount = 257;
        const int schema = 19;
        var validMask = new DirtyFieldMask(fieldCount);
        validMask.Set(0); validMask.Set(63); validMask.Set(64); validMask.Set(256);
        byte[] valid = DirtyFieldMaskCodec.Encode(schema, validMask, field => Enumerable.Repeat((byte)field, (field % 17) + 1).ToArray());

        // Fixed seeds make failures reproducible while exercising independent random streams. The
        // decoder must either produce a bounded, internally consistent result or reject the input
        // as InvalidDataException; parser implementation exceptions are test failures.
        foreach (int seed in new[] { 3, 29, 401, 9_973, 65_537 })
        {
            var random = new Random(seed);
            for (int iteration = 0; iteration < 1_000; iteration++)
            {
                byte[] candidate = new byte[random.Next(0, valid.Length + 96)];
                random.NextBytes(candidate);
                switch (iteration % 5)
                {
                    case 0:
                        candidate = valid.ToArray();
                        for (int mutation = 0; mutation < 1 + random.Next(12); mutation++)
                            candidate[random.Next(candidate.Length)] ^= (byte)random.Next(1, 256);
                        break;
                    case 1:
                        candidate = valid[..random.Next(valid.Length)].ToArray();
                        break;
                    case 2 when candidate.Length >= 8:
                        BinaryPrimitives.WriteInt32LittleEndian(candidate, random.Next(-4, 32));
                        BinaryPrimitives.WriteInt32LittleEndian(candidate.AsSpan(4), random.Next(-4, 512));
                        break;
                }

                try
                {
                    (int decodedSchema, DirtyFieldMask mask, IReadOnlyDictionary<int, byte[]> fields) = DirtyFieldMaskCodec.Decode(candidate, schema, fieldCount, maximumFieldBytes: 128);
                    Assert.Equal(schema, decodedSchema);
                    Assert.InRange(mask.FieldCount, 1, fieldCount);
                    Assert.All(fields, field =>
                    {
                        Assert.InRange(field.Key, 0, fieldCount - 1);
                        Assert.True(mask.IsSet(field.Key));
                        Assert.InRange(field.Value.Length, 0, 128);
                    });
                }
                catch (InvalidDataException)
                {
                    // Rejection is the expected safe outcome for malformed candidates.
                }
            }
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
        var metadata = new PredictionTickMetadata(TimeSpan.FromMilliseconds(16), 123, 4);
        var runtime = new PredictedWorldRuntime<int, float>(0, (state, input) => state + input, (from, to, amount) => from + (to - from) * amount, predictionMetadata: metadata);
        runtime.Predict(1, 2); runtime.Predict(2, 2); RollbackResult<float> correction = runtime.Reconcile(1, 1, 4);
        Assert.True(correction.Corrected); Assert.Equal(3, runtime.PredictedState); Assert.Equal(3.75f, runtime.RenderedState, 3); Assert.Equal(1, correction.ResimulatedTicks);
        Assert.Equal(metadata, runtime.PredictionMetadata); Assert.Equal(new[] { metadata }, runtime.LastReplayedMetadata);
        Assert.True(runtime.StepRenderedCorrection() < 3.75f);
    }

    [Fact]
    public void PredictedWorldRuntime_PassesMetadataToPredictionAndRollbackSimulation()
    {
        var seen = new List<PredictionTickMetadata>();
        var metadata = new PredictionTickMetadata(TimeSpan.FromMilliseconds(20), 17, 4);
        var runtime = new PredictedWorldRuntime<int, int>(0,
            (state, input, tickMetadata) => { seen.Add(tickMetadata); return state + input + (int)tickMetadata.DeterministicSeed; },
            (from, to, amount) => to, predictionMetadata: metadata);

        runtime.Predict(1, 1);
        runtime.Reconcile(0, 0);

        Assert.Equal(new[] { metadata, metadata }, seen);
        Assert.Equal(18, runtime.PredictedState);
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
