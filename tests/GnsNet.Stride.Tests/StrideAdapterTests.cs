namespace GnsNet.Stride.Tests;

using System.Numerics;
using global::Stride.Engine;
using Xunit;

public sealed class StrideAdapterTests
{
    [Fact]
    public void QueueShedsStateBeforeLifecycle()
    {
        var queue = new NetworkCommandQueue(2);
        Assert.True(queue.Enqueue(NetworkCommand.TransformSnapshot(1, 1, new NetworkTransformState(Vector3.Zero))));
        Assert.True(queue.Enqueue(NetworkCommand.TransformSnapshot(1, 2, new NetworkTransformState(Vector3.One))));
        Assert.True(queue.Enqueue(NetworkCommand.Spawn(2, 1)));
        Assert.Equal(1, queue.DroppedStateCount);
        Span<NetworkCommand> output = stackalloc NetworkCommand[2];
        Assert.Equal(2, queue.Drain(output));
        Assert.Contains(output.ToArray(), command => command.IsLifecycle);
    }

    [Fact]
    public void RegistryRejectsDuplicateAndMakesDespawnIdempotent()
    {
        var registry = new NetworkEntityViewRegistry();
        registry.BindUpdateThread();
        Assert.True(registry.TrySpawn(7, 1, false, () => new Entity("player"), out NetworkEntityView? view));
        Assert.NotNull(view);
        Assert.False(registry.TrySpawn(7, 1, false, () => new Entity("duplicate"), out _));
        Assert.True(registry.Despawn(7));
        Assert.False(registry.Despawn(7));
        registry.Dispose();
    }

    [Fact]
    public void ConversionPreservesQuaternionComponentOrder()
    {
        var source = new Quaternion(1, 2, 3, 4);
        var stride = StrideNumerics.ToStride(source);
        Assert.Equal(source, StrideNumerics.ToNumerics(stride));
    }

    [Fact]
    public void RemoteTransformFreezesAfterExtrapolationLimit()
    {
        var registry = new NetworkEntityViewRegistry();
        registry.BindUpdateThread();
        Assert.True(registry.TrySpawn(9, 1, false, () => new Entity("remote"), out NetworkEntityView? view));
        var processor = new NetworkTransformProcessor { MaximumExtrapolationTicks = 2 };
        processor.AddSnapshot(view!, 1, new NetworkTransformState(Vector3.Zero, Quaternion.Identity, Vector3.UnitX));
        processor.AddSnapshot(view!, 2, new NetworkTransformState(Vector3.One, Quaternion.Identity, Vector3.UnitX));
        Assert.True(processor.ApplyRemote(view!, 20));
        float frozen = view!.Entity.Transform.Position.X;
        Assert.True(processor.ApplyRemote(view, 21));
        Assert.Equal(frozen, view.Entity.Transform.Position.X);
    }

    [Fact]
    public void LocalCorrectionDoesNotReplaceSimulationState()
    {
        var registry = new NetworkEntityViewRegistry();
        registry.BindUpdateThread();
        Assert.True(registry.TrySpawn(11, 1, true, () => new Entity("local"), out NetworkEntityView? view));
        var processor = new LocalPredictionProcessor { TeleportThreshold = 100 };
        Assert.False(processor.Reconcile(view!, new Vector3(1, 0, 0), Vector3.Zero));
        processor.Update(view!, Vector3.Zero, 0.075);
        Assert.InRange(view!.Entity.Transform.Position.X, 0, 1);
        registry.Dispose();
    }

    [Fact]
    public void AnimationBindingAcceptsLatestSequenceOnly()
    {
        var registry = new NetworkEntityViewRegistry();
        registry.BindUpdateThread();
        Assert.True(registry.TrySpawn(13, 1, false, () => new Entity("animated"), out NetworkEntityView? view));
        var binding = new NetworkAnimationBinding();
        view!.Entity.Add(binding);
        var processor = new NetworkAnimationProcessor();
        Assert.True(processor.Apply(view, new NetworkAnimationState(1, .5f, true, 0, 0, 2, 1)));
        Assert.False(processor.Apply(view, new NetworkAnimationState(2, .8f, true, 0, 0, 1, 1)));
        Assert.Equal((ushort)1, binding.State.LocomotionId);
        registry.Dispose();
    }
}
