namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed partial class StateSyncTests
{
    [Fact]
    public void Prediction_ReplaysOnlyUnacknowledgedInputs()
    {
        var prediction = new ClientPrediction<int, int>();
        prediction.Add(1, 2);
        prediction.Add(2, 3);
        int result = prediction.Reconcile(1, 10, (state, input) => state + input);
        Assert.Equal(13, result);
        Assert.Equal(1, prediction.PendingCount);
    }

    [Fact]
    public void Prediction_ReplaysInputsInWraparoundOrder()
    {
        var prediction = new ClientPrediction<uint, string>(); prediction.Add(uint.MaxValue, 1); prediction.Add(0, 2);
        string result = prediction.Reconcile(uint.MaxValue - 1, "", (state, input) => state + input);
        Assert.Equal("12", result);
    }

    [Fact]
    public void Frame_RoundTripsOpcodeTickAndPayload()
    {
        var frame = new NetFrame(7, 42, [1, 2, 3]);
        var decoded = NetFrame.Decode(frame.Encode());
        Assert.Equal(frame.Opcode, decoded.Opcode);
        Assert.Equal(frame.Tick, decoded.Tick);
        Assert.Equal(frame.Payload, decoded.Payload);
    }

    [Fact]
    public void FrameDecoder_RejectsMalformedWireData()
    {
        Assert.Throws<InvalidDataException>(() => NetFrame.Decode([99, 1, 2]));
        Assert.Throws<InvalidDataException>(() => NetBatch.Decode([NetBatch.Magic, 1, 0, 0]));
    }

    [Fact]
    public void TypedRouter_RejectsCorruptSerializedPayload()
    {
        var router = new NetMessageRouter(); int calls = 0; router.Register<RouterState>(7, (_, _) => calls++);
        Assert.False(router.Dispatch(new NetFrame(7, 1, [0xFF, 0xFF]))); Assert.Equal(0, calls);
    }

    [MemoryPack.MemoryPackable]
    private partial class RouterState { public int Value { get; set; } }

    [Fact]
    public void LagCompensation_InterpolatesAndRejectsExpiredRewinds()
    {
        var history = new LagCompensationHistory<double>(TimeSpan.FromSeconds(10)); var origin = DateTimeOffset.UnixEpoch;
        history.Record(0, origin); history.Record(10, origin.AddSeconds(1));
        Assert.True(history.TryGet(origin.AddMilliseconds(500), (a, b, amount) => a + (b - a) * amount, out double state));
        Assert.Equal(5, state, 6);
        Assert.False(history.TryGet(origin.AddSeconds(-1), out _));
    }
}
