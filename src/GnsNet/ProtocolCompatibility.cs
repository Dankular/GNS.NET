namespace GnsNet;

/// <summary>Describes the wire contract a peer is willing to speak.</summary>
public readonly record struct ProtocolVersion(int Major, int Minor, int Schema)
{
    public override string ToString() => $"{Major}.{Minor}.{Schema}";
}

public readonly record struct ProtocolNegotiationResult(bool Accepted, ProtocolVersion Selected, string? Error = null);

/// <summary>Negotiates compatible protocol versions before gameplay messages are accepted.</summary>
public sealed class ProtocolCompatibility
{
    private readonly ProtocolVersion local;
    private readonly Func<ProtocolVersion, bool> schemaSupported;
    public ProtocolCompatibility(ProtocolVersion local, Func<ProtocolVersion, bool>? schemaSupported = null)
    { if (local.Major < 0 || local.Minor < 0 || local.Schema < 0) throw new ArgumentOutOfRangeException(nameof(local)); this.local = local; this.schemaSupported = schemaSupported ?? (v => v.Major == local.Major && v.Schema == local.Schema); }
    public ProtocolVersion Local => this.local;
    public ProtocolNegotiationResult Negotiate(ProtocolVersion remote)
    {
        if (remote.Major != this.local.Major) return new(false, default, "Protocol major versions differ.");
        ProtocolVersion selected = new(this.local.Major, Math.Min(this.local.Minor, remote.Minor), this.local.Schema);
        return this.schemaSupported(remote) ? new(true, selected) : new(false, default, "No compatible schema version.");
    }
}
