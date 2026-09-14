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
        Assert.Equal(2, GeneratedMessageMarker.GeneratedNetworkSchemaVersion);
        Assert.InRange(GeneratedMessageMarker.GeneratedNetworkMessageId, (ushort)1, ushort.MaxValue);
        Assert.Equal("marker.update", GeneratedMessageMarker.GeneratedRpcEndpoint);
        Assert.Equal(2, GeneratedMessageMarker.GeneratedNetworkFieldCount); Assert.Equal(new[] { "Health", "X" }, GeneratedMessageMarker.GeneratedNetworkFieldNames);
        Assert.Equal(2, GeneratedMessageMarker.GeneratedNetworkFieldIds.Length); Assert.NotEqual(GeneratedMessageMarker.GeneratedNetworkFieldIds[0], GeneratedMessageMarker.GeneratedNetworkFieldIds[1]);
        var mask = new DirtyFieldMask(GeneratedMessageMarker.GeneratedNetworkFieldCount); mask.Set(1);
        byte[] encoded = new GeneratedMessageMarker().EncodeGeneratedFields(mask, field => [(byte)field, 7]);
        Assert.Equal(new byte[] { 1, 7 }, GeneratedMessageMarker.DecodeGeneratedFields(encoded).Fields[1]);
        var source = new GeneratedMessageMarker { Health = 73, X = 12.5f };
        var generatedMask = new DirtyFieldMask(GeneratedMessageMarker.GeneratedNetworkFieldCount); generatedMask.Set(0); generatedMask.Set(1);
        byte[] generated = source.EncodeGeneratedFields(generatedMask);
        var restored = new GeneratedMessageMarker(); restored.ApplyGeneratedFields(generated);
        Assert.Equal(source.Health, restored.Health); Assert.Equal(source.X, restored.X);
        var router = new RpcRouter(); router.SetOwner(1, "client"); GeneratedMessageMarker.RegisterGeneratedRpc(router, request => new(request.RequestId, true, request.Payload));
        Assert.True(router.Dispatch(new(Guid.NewGuid(), "marker.update", 1, "client", [1], 1)).Accepted);
    }
}
