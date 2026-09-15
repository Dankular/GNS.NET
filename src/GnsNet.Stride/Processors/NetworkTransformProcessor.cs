namespace GnsNet.Stride;

using System.Numerics;
using GnsNet;
using global::Stride.Engine;

/// <summary>Applies buffered remote transforms and bounded local presentation transforms.</summary>
public sealed class NetworkTransformProcessor
{
    private readonly Dictionary<ulong, TransformTrack> tracks = new();

    /// <summary>Maximum extrapolation window in server ticks.</summary>
    public uint MaximumExtrapolationTicks { get; init; } = 2;

    /// <summary>Stores an authoritative snapshot for later render sampling.</summary>
    public void AddSnapshot(NetworkEntityView view, uint tick, NetworkTransformState state)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (!this.tracks.TryGetValue(view.NetworkId, out TransformTrack? track)) this.tracks.Add(view.NetworkId, track = new TransformTrack());
        track.Add(tick, state);
    }

    /// <summary>Samples a remote view; extrapolation stops at the configured limit and then freezes.</summary>
    public bool ApplyRemote(NetworkEntityView view, double renderTick)
    {
        if (!this.tracks.TryGetValue(view.NetworkId, out TransformTrack? track) || !track.TrySample(renderTick, this.MaximumExtrapolationTicks, out NetworkTransformState state)) return false;
        view.Entity.Transform.Position = StrideNumerics.ToStride(state.Position);
        view.Entity.Transform.Rotation = StrideNumerics.ToStride(state.Rotation);
        view.Entity.Transform.Scale = new global::Stride.Core.Mathematics.Vector3(1, 1, 1);
        return true;
    }

    /// <summary>Applies a local visual position while keeping simulation state outside Stride.</summary>
    public static void ApplyPresentation(NetworkEntityView view, Vector3 simulationPosition, Vector3 visualCorrection)
    {
        view.Entity.Transform.Position = StrideNumerics.ToStride(simulationPosition + visualCorrection);
    }

    /// <summary>Removes history for a despawned view.</summary>
    public void Remove(ulong networkId) => this.tracks.Remove(networkId);

    private sealed class TransformTrack
    {
        private readonly SnapshotBuffer<NetworkTransformState> snapshots = new(8);
        private NetworkTransformState previous;
        private NetworkTransformState latest;
        private uint previousTick;
        private uint latestTick;
        private bool initialized;

        public void Add(uint tick, NetworkTransformState state)
        {
            if (this.initialized && !TickSequence.IsNewer(this.latestTick, tick)) return;
            this.previous = this.latest; this.previousTick = this.latestTick; this.latest = state; this.latestTick = tick; this.initialized = true;
            this.snapshots.Add(tick, state);
        }

        public bool TrySample(double tick, uint maximumExtrapolation, out NetworkTransformState state)
        {
            if (this.snapshots.TrySample((uint)Math.Max(0, Math.Floor(tick)), static (a, b, alpha) => new NetworkTransformState(
                Vector3.Lerp(a.Position, b.Position, alpha), Quaternion.Slerp(a.Rotation, b.Rotation, alpha), Vector3.Lerp(a.Velocity, b.Velocity, alpha)), out state)) return true;
            if (!this.initialized) { state = default; return false; }
            uint elapsed = tick <= this.latestTick ? 0 : (uint)Math.Min(tick - this.latestTick, maximumExtrapolation);
            state = elapsed == 0 || this.previousTick == this.latestTick ? this.latest : new NetworkTransformState(
                this.latest.Position + this.latest.Velocity * ((this.latestTick - this.previousTick) <= 0 ? 0 : elapsed / 30f),
                this.latest.Rotation,
                this.latest.Velocity);
            return true;
        }
    }
}
