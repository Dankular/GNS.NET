namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public class TickSequenceTests
{
    [Fact]
    public void IsNewer_TrueForGreaterTick()
    {
        Assert.True(TickSequence.IsNewer(baseline: 5, candidate: 6));
    }

    [Fact]
    public void IsNewer_FalseForEqualOrOlderTick()
    {
        Assert.False(TickSequence.IsNewer(baseline: 5, candidate: 5));
        Assert.False(TickSequence.IsNewer(baseline: 5, candidate: 4));
    }

    [Fact]
    public void IsNewer_HandlesWraparound()
    {
        // Just before the uint wraps back to 0.
        uint baseline = uint.MaxValue;
        uint candidate = 0;

        Assert.True(TickSequence.IsNewer(baseline, candidate));
    }

    [Fact]
    public void IsNewer_RejectsFarPastAsNotNewer()
    {
        // Half the sequence space away or more is treated as "before", per RFC 1982.
        uint baseline = 100;
        uint candidate = baseline + (uint.MaxValue / 2) + 1;

        Assert.False(TickSequence.IsNewer(baseline, candidate));
    }
}
