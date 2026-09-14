namespace GnsNet;

using System.Net;
using System.Security.Cryptography;
using System.Text;

/// <summary>Configuration for one coturn server using TURN REST API shared-secret authentication.</summary>
public sealed record TurnRelayServer
{
    public Uri Url { get; }
    public string SharedSecret { get; }
    public string? PublicAddress { get; }
    public TurnRelayServer(Uri url, string sharedSecret, string? publicAddress = null)
    {
        if (url is null || !string.Equals(url.Scheme, "turn", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("TURN URL must use the turn scheme.", nameof(url));
        if (string.IsNullOrWhiteSpace(sharedSecret)) throw new ArgumentException("TURN shared secret is required.", nameof(sharedSecret));
        Url = url; SharedSecret = sharedSecret; PublicAddress = publicAddress;
    }
}

/// <summary>A short-lived TURN credential and its relay endpoint.</summary>
public sealed record TurnCredential(P2PRelayEndpoint Endpoint, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates coturn TURN REST credentials. The shared secret is used only to derive a short-lived
/// HMAC credential and is never included in the returned endpoint or signaling payload.
/// </summary>
public sealed class TurnCredentialRotator
{
    private readonly IReadOnlyList<TurnRelayServer> servers;
    private readonly TimeSpan lifetime;
    public TurnCredentialRotator(IEnumerable<TurnRelayServer> servers, TimeSpan? lifetime = null)
    {
        this.servers = servers?.ToArray() ?? throw new ArgumentNullException(nameof(servers));
        if (this.servers.Count == 0) throw new ArgumentException("At least one TURN server is required.", nameof(servers));
        this.lifetime = lifetime ?? TimeSpan.FromMinutes(10);
        if (this.lifetime < TimeSpan.FromMinutes(1) || this.lifetime > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lifetime));
    }

    /// <summary>Issues one credential per configured relay, allowing deterministic fallback.</summary>
    public IReadOnlyList<TurnCredential> Issue(string user, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Contains(':', StringComparison.Ordinal)) throw new ArgumentException("TURN user must be non-empty and contain no colon.", nameof(user));
        DateTimeOffset issued = now ?? DateTimeOffset.UtcNow;
        long expiry = issued.Add(this.lifetime).ToUnixTimeSeconds();
        string username = $"{expiry}:{user}";
        return this.servers.Select(server =>
        {
            byte[] digest = HMACSHA1.HashData(Encoding.UTF8.GetBytes(server.SharedSecret), Encoding.UTF8.GetBytes(username));
            string credential = Convert.ToBase64String(digest);
            var endpoint = new P2PRelayEndpoint(server.Url.ToString(), username, credential, issued.Add(this.lifetime));
            return new TurnCredential(endpoint, endpoint.ExpiresAt);
        }).ToArray();
    }

    /// <summary>Returns non-expired relays in configured order; callers can try each in order.</summary>
    public static IReadOnlyList<P2PRelayEndpoint> SelectFallback(IEnumerable<P2PRelayEndpoint> relays, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(relays); DateTimeOffset current = now ?? DateTimeOffset.UtcNow;
        return relays.Where(x => x.ExpiresAt > current && !string.IsNullOrWhiteSpace(x.Username) && !string.IsNullOrWhiteSpace(x.Credential)).ToArray();
    }
}
