namespace GnsNet;

/// <summary>Optional native GNS ICE candidate and STUN configuration.</summary>
public sealed class GnsP2POptions
{
    /// <summary>Native GNS ICE candidate policy value; interpretation follows the selected GNS build.</summary>
    public int? IceCandidatePolicy { get; init; }
    /// <summary>Comma-separated STUN server list, or null to retain the native default.</summary>
    public string? StunServerList { get; init; }
    /// <summary>Short-lived TURN credentials passed to native GNS as aligned server/user/password lists.</summary>
    /// <remarks>Credentials are secret material and must come from a protected runtime configuration source.</remarks>
    public IReadOnlyList<P2PRelayEndpoint>? TurnRelays { get; init; }
    public void Validate()
    {
        if (IceCandidatePolicy is < 0) throw new ArgumentOutOfRangeException(nameof(IceCandidatePolicy));
        if (StunServerList?.Length > 4096) throw new ArgumentException("The STUN server list is too long.", nameof(StunServerList));
        if (TurnRelays is null) return;
        foreach (P2PRelayEndpoint relay in TurnRelays)
        {
            if (!Uri.TryCreate(relay.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("turn" or "turns"))
                throw new ArgumentException("TURN relay URLs must be absolute turn: or turns: URLs.", nameof(TurnRelays));
            if (relay.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("TURN relay credentials must not be expired.", nameof(TurnRelays));
            if (string.IsNullOrWhiteSpace(relay.Username) || string.IsNullOrWhiteSpace(relay.Credential))
                throw new ArgumentException("TURN relay credentials require both username and credential.", nameof(TurnRelays));
            if (relay.Url.Contains(',', StringComparison.Ordinal) || relay.Username.Contains(',', StringComparison.Ordinal) || relay.Credential.Contains(',', StringComparison.Ordinal))
                throw new ArgumentException("TURN relay values cannot contain commas.", nameof(TurnRelays));
        }
    }
}
