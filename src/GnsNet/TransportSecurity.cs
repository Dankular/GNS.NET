namespace GnsNet;

using GnsSharp;

public readonly record struct NativeAuthenticationCapabilities(bool CertificateTransportValidation, bool SteamAuthTickets, string Limitation);

/// <summary>Reports native authentication features available from the selected GnsSharp backend.</summary>
public static class NativeAuthentication
{
    public static NativeAuthenticationCapabilities Capabilities { get; } = new(
        CertificateTransportValidation: true,
        SteamAuthTickets: false,
        Limitation: "Steam BeginAuthSession ticket callbacks require the Steamworks backend; this build targets open-source GNS.");
}

/// <summary>Enforces the native GNS authenticated/encrypted connection contract.</summary>
public sealed class TransportSecurityPolicy
{
    public bool RequireAuthenticated { get; init; } = true;
    public bool RequireEncrypted { get; init; } = true;
    public bool Accept(SteamNetConnectionInfo_t info, out string? reason)
    {
        if (this.RequireAuthenticated && info.Flags.HasFlag(ESteamNetworkConnectionInfoFlags.Unauthenticated)) { reason = "Native GNS authentication is required."; return false; }
        if (this.RequireEncrypted && info.Flags.HasFlag(ESteamNetworkConnectionInfoFlags.Unencrypted)) { reason = "Encrypted GNS transport is required."; return false; }
        reason = null; return true;
    }
}
