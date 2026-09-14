namespace GnsNet.Tests;

using GnsNet;
using Xunit;

[GenerateNetSchema(2)]
[GenerateNetMessage]
[GenerateNetRpc("marker.update", RpcAuthority.OwnerOnly)]
public partial class GeneratedMessageMarker { public int Health { get; set; } public float X { get; set; } }

public sealed class GeneratorTests
{
    [Fact]
    public void SourceGenerator_EmitsSchemaVersionForAnnotatedPartialType()
    {
        Assert.Equal(1, GeneratedMessageMarker.GeneratedNetworkSchemaVersion);
        Assert.InRange(GeneratedMessageMarker.GeneratedNetworkMessageId, (ushort)1, ushort.MaxValue);
        Assert.Equal("marker.update", GeneratedMessageMarker.GeneratedRpcEndpoint);
        Assert.Equal(2, GeneratedMessageMarker.GeneratedNetworkFieldCount); Assert.Equal(new[] { "Health", "X" }, GeneratedMessageMarker.GeneratedNetworkFieldNames);
        var router = new RpcRouter(); router.SetOwner(1, "client"); GeneratedMessageMarker.RegisterGeneratedRpc(router, request => new(request.RequestId, true, request.Payload));
        Assert.True(router.Dispatch(new(Guid.NewGuid(), "marker.update", 1, "client", [1], 1)).Accepted);
    }
}
