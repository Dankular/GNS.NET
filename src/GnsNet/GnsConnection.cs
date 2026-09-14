namespace GnsNet;

using GnsSharp;

/// <summary>
/// A connection accepted by <see cref="GnsServer"/> or opened by <see cref="GnsClient"/>.
/// Thin wrapper around the underlying <see cref="HSteamNetConnection"/> handle.
/// </summary>
public sealed class GnsConnection : INativeTransportDiagnostics
{
    internal GnsConnection(HSteamNetConnection handle)
    {
        this.Handle = handle;
    }

    public HSteamNetConnection Handle { get; }

    internal ISteamNetworkingSockets Sockets { get; init; } = null!;

    public NativeConnectionStatistics GetStatistics() => NativeTransportDiagnostics.GetStatistics(this.Sockets, this.Handle);
    public IReadOnlyList<NativeLaneStatistics> GetLaneStatistics(int laneCount) => NativeTransportDiagnostics.GetLaneStatistics(this.Sockets, this.Handle, laneCount);
    public EResult ConfigureLanes(ReadOnlySpan<int> priorities, ReadOnlySpan<ushort> weights = default)
        => NativeTransportDiagnostics.ConfigureLanes(this.Sockets, this.Handle, priorities, weights);
    public EResult SendOnLane(ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType, ushort lane)
        => NativeTransportDiagnostics.SendOnLane(this.Sockets, this.Handle, data, sendType, lane);

    public override string ToString() => this.Handle.ToString();

    public override bool Equals(object? obj) => obj is GnsConnection other && other.Handle == this.Handle;

    public override int GetHashCode() => this.Handle.GetHashCode();
}
