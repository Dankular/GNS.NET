namespace GnsNet.Tests;

using GnsNet;
using Xunit;

[GenerateNetSchema(2)]
[GenerateNetMessage]
[GenerateNetRpc("marker.update", RpcAuthority.OwnerOnly)]
public partial class GeneratedMessageMarker { }

public sealed class GeneratorTests
{
    [Fact]
    public void SourceGenerator_EmitsSchemaVersionForAnnotatedPartialType()
    {
        Assert.Equal(1, GeneratedMessageMarker.GeneratedNetworkSchemaVersion);
        Assert.InRange(GeneratedMessageMarker.GeneratedNetworkMessageId, (ushort)1, ushort.MaxValue);
        Assert.Equal("marker.update", GeneratedMessageMarker.GeneratedRpcEndpoint);
    }
}
