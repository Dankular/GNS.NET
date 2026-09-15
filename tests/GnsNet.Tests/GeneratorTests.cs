namespace GnsNet.Tests;

using GnsNet;
using GnsNet.Replication;
using System.Numerics;
using Xunit;

[GenerateNetSchema(2)]
[GenerateNetMessage]
[GenerateNetRpc("marker.update", RpcAuthority.OwnerOnly)]
public partial class GeneratedMessageMarker { public int Health { get; set; } public float X { get; set; } }

[ReplicatedComponent(401, SchemaVersion = 2)]
public partial struct GeneratedReplicationMarker
{
    [ReplicatedField(9, Quantize = 0.01f, Threshold = 0.005f)] public Vector3 Position { get; set; }
    [ReplicatedField(2)] public ushort Health;
    [ReplicatedField(7, Interpolation = InterpolationMode.Spherical)] public Quaternion Rotation { get; set; }
}

public sealed class GeneratorTests
{
    [Fact]
    public void ReplicationGenerator_EmitsStableDescriptorAndBoundedFullAndDeltaCodecs()
    {
        Assert.Equal((ushort)401, GeneratedReplicationMarker.ReplicationCodec.ComponentId);
        Assert.Equal(new byte[] { 2, 7, 9 }, GeneratedReplicationMarker.GeneratedReplicationDescriptor.Fields.Select(field => field.FieldId).ToArray());
        var current = new GeneratedReplicationMarker { Health = 75, Position = new(1.234f, 2, 3), Rotation = Quaternion.Identity };
        var writer = new ReplicationWriter(); GeneratedReplicationMarker.ReplicationCodec.WriteFull(ref writer, in current);
        var reader = new ReplicationReader(writer.WrittenMemory.Span);
        Assert.True(GeneratedReplicationMarker.ReplicationCodec.TryReadFull(ref reader, out GeneratedReplicationMarker restored));
        Assert.Equal(current.Health, restored.Health); Assert.Equal(new Vector3(1.23f, 2, 3), restored.Position);
        var baseline = restored; current.Health = 80;
        DirtyFieldMask mask = GeneratedReplicationMarker.ReplicationCodec.Diff(in baseline, in current);
        Assert.True(mask.IsSet(0)); Assert.False(mask.IsSet(1)); Assert.False(mask.IsSet(2));
        writer = new ReplicationWriter(); GeneratedReplicationMarker.ReplicationCodec.WriteDelta(ref writer, in baseline, in current, in mask);
        reader = new ReplicationReader(writer.WrittenMemory.Span);
        Assert.True(GeneratedReplicationMarker.ReplicationCodec.TryApplyDelta(ref reader, in baseline, out restored));
        Assert.Equal((ushort)80, restored.Health);
    }

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
