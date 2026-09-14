namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public sealed class ProtocolCompatibilityTests
{
    [Fact]
    public void Negotiation_SelectsCommonMinorAndRejectsMajorOrSchemaMismatch()
    {
        var compatibility = new ProtocolCompatibility(new ProtocolVersion(2, 4, 7));
        ProtocolNegotiationResult result = compatibility.Negotiate(new ProtocolVersion(2, 2, 7));
        Assert.True(result.Accepted); Assert.Equal(new ProtocolVersion(2, 2, 7), result.Selected);
        Assert.False(compatibility.Negotiate(new ProtocolVersion(3, 0, 7)).Accepted);
        Assert.False(compatibility.Negotiate(new ProtocolVersion(2, 9, 8)).Accepted);
    }
}
