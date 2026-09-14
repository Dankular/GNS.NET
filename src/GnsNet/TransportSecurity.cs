namespace GnsNet;

using GnsSharp;

public readonly record struct NativeAuthenticationCapabilities(bool CertificateTransportValidation, bool CertificateProvisioning, bool SteamAuthTickets, string Limitation);

/// <summary>Reports native authentication features available from the selected GnsSharp backend.</summary>
public static class NativeAuthentication
{
    public static NativeAuthenticationCapabilities Capabilities { get; } = new(
        CertificateTransportValidation: true,
        CertificateProvisioning: true,
        SteamAuthTickets: false,
        Limitation: "The open-source backend validates GNS certificates, but does not expose Steam BeginAuthSession ticket callbacks. Application join claims remain required for game authorization.");

    /// <summary>Installs a coordinator-issued SteamDatagram certificate on the native socket interface.</summary>
    public static void SetCertificate(ISteamNetworkingSockets sockets, ReadOnlySpan<byte> certificate)
    {
        if (certificate.IsEmpty) throw new ArgumentException("A non-empty SteamDatagram certificate is required.", nameof(certificate));
        ArgumentNullException.ThrowIfNull(sockets);
        if (!sockets.SetCertificate(certificate, out string? error)) throw new InvalidOperationException($"Native GNS certificate was rejected: {error ?? "unknown error"}.");
    }

    /// <summary>Obtains the native certificate request blob to send to a game coordinator.</summary>
    public static byte[] CreateCertificateRequest(ISteamNetworkingSockets sockets)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        int size = 0;
        sockets.GetCertificateRequest(ref size, Span<byte>.Empty, out string? probeError);
        if (size < 1) throw new InvalidOperationException($"Native GNS certificate request is unavailable: {probeError ?? "unknown error"}.");
        byte[] request = new byte[size];
        if (!sockets.GetCertificateRequest(ref size, request, out string? error)) throw new InvalidOperationException($"Native GNS certificate request failed: {error ?? "unknown error"}.");
        return request.AsSpan(0, size).ToArray();
    }

    /// <summary>Queries the actual native authentication readiness and diagnostic status.</summary>
    public static ESteamNetworkingAvailability GetStatus(ISteamNetworkingSockets sockets, out SteamNetAuthenticationStatus_t status)
    {
        ArgumentNullException.ThrowIfNull(sockets);
        return sockets.GetAuthenticationStatus(out status);
    }
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
