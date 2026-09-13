namespace GnsNet;

/// <summary>Optional native GNS ICE candidate and STUN configuration.</summary>
public sealed class GnsP2POptions
{
    /// <summary>Native GNS ICE candidate policy value; interpretation follows the selected GNS build.</summary>
    public int? IceCandidatePolicy { get; init; }
    /// <summary>Comma-separated STUN server list, or null to retain the native default.</summary>
    public string? StunServerList { get; init; }
    public void Validate()
    {
        if (IceCandidatePolicy is < 0) throw new ArgumentOutOfRangeException(nameof(IceCandidatePolicy));
        if (StunServerList?.Length > 4096) throw new ArgumentException("The STUN server list is too long.", nameof(StunServerList));
    }
}
