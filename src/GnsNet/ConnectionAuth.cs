namespace GnsNet;

using System.Security.Cryptography;
using System.Text;

public readonly record struct ConnectClaims(string SessionId, DateTimeOffset ExpiresAt, string Nonce);

/// <summary>Issues and validates short-lived HMAC-signed connection tickets.</summary>
public sealed class ConnectTokenService
{
    private readonly byte[] secret;
    public ConnectTokenService(ReadOnlySpan<byte> secret)
    {
        if (secret.Length < 32) throw new ArgumentException("Use at least a 256-bit secret.", nameof(secret));
        this.secret = secret.ToArray();
    }
    public string Issue(string sessionId, TimeSpan lifetime, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Contains('|')) throw new ArgumentException("A non-empty session id without '|' is required.", nameof(sessionId));
        var expires = (now ?? DateTimeOffset.UtcNow).Add(lifetime).ToUnixTimeSeconds();
        string body = $"{sessionId}|{expires}|{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        return Encode(body) + "." + Encode(Sign(body));
    }
    public bool TryValidate(string token, out ConnectClaims claims, DateTimeOffset? now = null)
    {
        claims = default;
        string[] parts = token.Split('.', 2);
        if (parts.Length != 2) return false;
        string body;
        try { body = Decode(parts[0]); } catch (FormatException) { return false; }
        byte[] provided;
        try { provided = DecodeBytes(parts[1]); } catch (FormatException) { return false; }
        if (!CryptographicOperations.FixedTimeEquals(provided, Sign(body))) return false;
        string[] fields = body.Split('|');
        if (fields.Length != 3 || !long.TryParse(fields[1], out long seconds)) return false;
        DateTimeOffset expires; try { expires = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { return false; }
        if (expires <= (now ?? DateTimeOffset.UtcNow)) return false;
        claims = new ConnectClaims(fields[0], expires, fields[2]);
        return true;
    }
    private byte[] Sign(string body) => HMACSHA256.HashData(this.secret, Encoding.UTF8.GetBytes(body));
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Encode(byte[] value) => Convert.ToBase64String(value).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    private static string Decode(string value) => Encoding.UTF8.GetString(DecodeBytes(value));
    private static byte[] DecodeBytes(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

public sealed class ConnectionAdmission
{
    private readonly ConnectTokenService tokens;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> usedNonces = new();
    public ConnectionAdmission(ConnectTokenService tokens) => this.tokens = tokens;
    public bool TryAdmit(string token, out ConnectClaims claims)
    {
        if (!this.tokens.TryValidate(token, out claims)) return false;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (var item in this.usedNonces) if (item.Value <= now) this.usedNonces.TryRemove(item.Key, out _);
        return this.usedNonces.TryAdd(claims.Nonce, claims.ExpiresAt);
    }
    /// <summary>Releases a ticket nonce when its authenticated connection ends, allowing resumption before expiry.</summary>
    public bool Release(string nonce) => !string.IsNullOrWhiteSpace(nonce) && this.usedNonces.TryRemove(nonce, out _);
}
