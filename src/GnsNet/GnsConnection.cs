namespace GnsNet;

using GnsSharp;

/// <summary>
/// A connection accepted by <see cref="GnsServer"/> or opened by <see cref="GnsClient"/>.
/// Thin wrapper around the underlying <see cref="HSteamNetConnection"/> handle.
/// </summary>
public sealed class GnsConnection
{
    internal GnsConnection(HSteamNetConnection handle)
    {
        this.Handle = handle;
    }

    public HSteamNetConnection Handle { get; }

    public override string ToString() => this.Handle.ToString();

    public override bool Equals(object? obj) => obj is GnsConnection other && other.Handle == this.Handle;

    public override int GetHashCode() => this.Handle.GetHashCode();
}
