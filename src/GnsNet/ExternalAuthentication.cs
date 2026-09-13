namespace GnsNet;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Provider-neutral identity returned after an external credential is verified.</summary>
public readonly record struct ExternalIdentity(string Provider, string Subject, IReadOnlyDictionary<string, string> Claims);

/// <summary>Verifies a provider credential without coupling the transport to a provider SDK.</summary>
public interface IExternalIdentityVerifier
{
    ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default);
}

/// <summary>Converts an externally verified identity into the GNS.NET admission token used by the transport.</summary>
public sealed class AuthenticationGateway
{
    private readonly ConnectTokenService tokens;
    public AuthenticationGateway(ConnectTokenService tokens) => this.tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    public async ValueTask<string?> AuthenticateAsync(IExternalIdentityVerifier verifier, string credential, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ExternalIdentity? identity = await verifier.VerifyAsync(credential, cancellationToken).ConfigureAwait(false);
        return identity is null ? null : this.tokens.Issue($"{identity.Value.Provider}:{identity.Value.Subject}", lifetime);
    }
}

/// <summary>Validates RS256 JWTs against an issuer, audience, time claims, and a configured JWKS key set.</summary>
public sealed class JwtIdentityVerifier : IExternalIdentityVerifier
{
    private readonly string issuer;
    private readonly string audience;
    private readonly IReadOnlyDictionary<string, RSA> keys;
    public JwtIdentityVerifier(string provider, string issuer, string audience, IReadOnlyDictionary<string, RSA> keys)
    {
        this.Provider = string.IsNullOrWhiteSpace(provider) ? throw new ArgumentException("Provider is required.", nameof(provider)) : provider;
        this.issuer = issuer ?? throw new ArgumentNullException(nameof(issuer)); this.audience = audience ?? throw new ArgumentNullException(nameof(audience)); this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }
    public string Provider { get; }
    public ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string[] parts = credential.Split('.'); if (parts.Length != 3) return new((ExternalIdentity?)null);
            using JsonDocument header = JsonDocument.Parse(Base64UrlDecode(parts[0])); using JsonDocument payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            JsonElement h = header.RootElement, p = payload.RootElement;
            if (!h.TryGetProperty("alg", out JsonElement alg) || alg.GetString() != "RS256" || !h.TryGetProperty("kid", out JsonElement kid) || !this.keys.TryGetValue(kid.GetString() ?? "", out RSA? key)) return new((ExternalIdentity?)null);
            byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]); byte[] signature = Base64UrlDecode(parts[2]);
            if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return new((ExternalIdentity?)null);
            if (!p.TryGetProperty("iss", out JsonElement iss) || iss.GetString() != this.issuer || !HasAudience(p, this.audience) || !p.TryGetProperty("sub", out JsonElement subject)) return new((ExternalIdentity?)null);
            DateTimeOffset now = DateTimeOffset.UtcNow; if (!ValidTime(p, "exp", now, false) || !ValidTime(p, "nbf", now, true)) return new((ExternalIdentity?)null);
            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in p.EnumerateObject()) if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) claims[property.Name] = property.Value.ToString();
            return new(new ExternalIdentity(this.Provider, subject.GetString() ?? "", claims));
        }
        catch (Exception) { return new((ExternalIdentity?)null); }
    }
    private static bool HasAudience(JsonElement payload, string expected) => payload.TryGetProperty("aud", out JsonElement aud) && (aud.ValueKind == JsonValueKind.String ? aud.GetString() == expected : aud.ValueKind == JsonValueKind.Array && aud.EnumerateArray().Any(x => x.GetString() == expected));
    private static bool ValidTime(JsonElement payload, string name, DateTimeOffset now, bool optional) => !payload.TryGetProperty(name, out JsonElement value) ? optional : value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long seconds) && (name == "exp" ? DateTimeOffset.FromUnixTimeSeconds(seconds) > now : DateTimeOffset.FromUnixTimeSeconds(seconds) <= now.AddSeconds(30));
    private static byte[] Base64UrlDecode(string value) => Base64UrlDecodeForJwks(value);
    internal static byte[] Base64UrlDecodeForJwks(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

/// <summary>Loads RSA signing keys from a JWKS document for use by <see cref="JwtIdentityVerifier"/>.</summary>
public static class JwksLoader
{
    public static async Task<IReadOnlyDictionary<string, RSA>> LoadAsync(HttpClient client, Uri endpoint, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = await client.GetFromJsonAsync<JsonDocument>(endpoint, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("JWKS response was empty.");
        var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        foreach (JsonElement item in document.RootElement.GetProperty("keys").EnumerateArray())
        {
            if (item.GetProperty("kty").GetString() != "RSA" || item.GetProperty("n").GetString() is not string modulus || item.GetProperty("e").GetString() is not string exponent) continue;
            var rsa = RSA.Create(); rsa.ImportParameters(new RSAParameters { Modulus = JwtIdentityVerifier.Base64UrlDecodeForJwks(modulus), Exponent = JwtIdentityVerifier.Base64UrlDecodeForJwks(exponent) }); keys[item.GetProperty("kid").GetString() ?? throw new InvalidDataException("JWKS key has no kid.")] = rsa;
        }
        return keys;
    }
}

/// <summary>Verifies provider credentials through a trusted backend validation endpoint.</summary>
public class HttpIdentityVerifier : IExternalIdentityVerifier
{
    private readonly HttpClient client; private readonly Uri endpoint;
    public HttpIdentityVerifier(string provider, HttpClient client, Uri endpoint)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        this.Provider = provider; this.client = client ?? throw new ArgumentNullException(nameof(client)); this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (this.endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Identity validation endpoints must use HTTPS.", nameof(endpoint));
    }
    public string Provider { get; }
    public async ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await this.client.PostAsJsonAsync(this.endpoint, new { credential }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Identity response was empty.");
        JsonElement root = document.RootElement; if (!root.TryGetProperty("subject", out JsonElement subject) || string.IsNullOrWhiteSpace(subject.GetString())) return null;
        var claims = new Dictionary<string, string>(StringComparer.Ordinal); if (root.TryGetProperty("claims", out JsonElement claimObject) && claimObject.ValueKind == JsonValueKind.Object) foreach (JsonProperty claim in claimObject.EnumerateObject()) claims[claim.Name] = claim.Value.ToString();
        return new ExternalIdentity(this.Provider, subject.GetString()!, claims);
    }
}

public sealed class EosIdentityVerifier(HttpClient client, Uri endpoint) : HttpIdentityVerifier("eos", client, endpoint);
public sealed class PlayFabIdentityVerifier(HttpClient client, Uri endpoint) : HttpIdentityVerifier("playfab", client, endpoint);
public sealed class XboxXstsIdentityVerifier(HttpClient client, Uri endpoint) : HttpIdentityVerifier("xbox-xsts", client, endpoint);
public sealed class GooglePlayGamesIdentityVerifier(HttpClient client, Uri endpoint) : HttpIdentityVerifier("google-play-games", client, endpoint);
public sealed class AppleGameCenterIdentityVerifier(HttpClient client, Uri endpoint) : HttpIdentityVerifier("apple-game-center", client, endpoint);

/// <summary>Expected EOS identity context for a Connect credential validation request.</summary>
public sealed class EosConnectValidationOptions
{
    public string? DeploymentId { get; init; }
    public string? SandboxId { get; init; }
    public string? ClientId { get; init; }
    public string? Nonce { get; init; }
}

/// <summary>
/// Validates an EOS Connect credential through an EOS-backed HTTPS service and enforces the
/// product/deployment context before issuing a GNS.NET identity.
/// </summary>
/// <remarks>
/// The service endpoint must use the EOS SDK or EOS service APIs to validate the credential. It
/// receives <c>credential</c> and the expected context, and returns a JSON object containing
/// <c>valid</c>, <c>productUserId</c>, <c>deploymentId</c>, <c>sandboxId</c>, <c>clientId</c>,
/// <c>expiresAt</c>, and (when a nonce was supplied) <c>nonce</c>. This keeps EOS SDK licensing and
/// native binaries out of GnsNet while preventing an untrusted subject from becoming an identity.
/// </remarks>
public sealed class EosConnectIdentityVerifier : IExternalIdentityVerifier
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly EosConnectValidationOptions expected;
    public EosConnectIdentityVerifier(HttpClient client, Uri endpoint, EosConnectValidationOptions? expected = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client)); this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (this.endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("EOS validation endpoints must use HTTPS.", nameof(endpoint));
        this.expected = expected ?? new EosConnectValidationOptions();
    }
    public async ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        using HttpResponseMessage response = await this.client.PostAsJsonAsync(this.endpoint, new { credential, expected = this.expected }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("EOS response was empty.");
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("valid", out JsonElement valid) || valid.ValueKind != JsonValueKind.True || !root.TryGetProperty("productUserId", out JsonElement productUserId) || string.IsNullOrWhiteSpace(productUserId.GetString())) return null;
        if (!Matches(root, "deploymentId", this.expected.DeploymentId) || !Matches(root, "sandboxId", this.expected.SandboxId) || !Matches(root, "clientId", this.expected.ClientId) || !Matches(root, "nonce", this.expected.Nonce)) return null;
        if (!root.TryGetProperty("expiresAt", out JsonElement expiry) || !DateTimeOffset.TryParse(expiry.GetString(), out DateTimeOffset expiresAt) || expiresAt <= DateTimeOffset.UtcNow) return null;
        var claims = new Dictionary<string, string>(StringComparer.Ordinal) { ["product_user_id"] = productUserId.GetString()!, ["expires_at"] = expiresAt.ToString("O") };
        foreach (string name in new[] { "deploymentId", "sandboxId", "clientId", "nonce" }) if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) claims[name] = value.GetString()!;
        return new ExternalIdentity("eos-connect", productUserId.GetString()!, claims);
    }
    private static bool Matches(JsonElement root, string name, string? expected) => expected is null || root.TryGetProperty(name, out JsonElement actual) && actual.GetString() == expected;
}

/// <summary>Validates a PlayFab client session ticket using the PlayFab Server API.</summary>
/// <remarks>
/// The title secret key stays server-side and is sent only as the X-SecretKey header. PlayFab
/// documents this endpoint as POST /Server/AuthenticateSessionTicket on the title API hostname.
/// </remarks>
public sealed class PlayFabSessionTicketVerifier : IExternalIdentityVerifier
{
    private readonly HttpClient client;
    private readonly string secretKey;
    private readonly Uri endpoint;
    public PlayFabSessionTicketVerifier(string titleId, string secretKey, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(titleId) || titleId.Any(char.IsWhiteSpace)) throw new ArgumentException("A PlayFab title id is required.", nameof(titleId));
        if (string.IsNullOrWhiteSpace(secretKey)) throw new ArgumentException("A PlayFab secret key is required.", nameof(secretKey));
        this.secretKey = secretKey; this.client = client ?? new HttpClient();
        this.endpoint = new Uri($"https://{titleId}.playfabapi.com/Server/AuthenticateSessionTicket", UriKind.Absolute);
    }
    public async ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        using var request = new HttpRequestMessage(HttpMethod.Post, this.endpoint) { Content = JsonContent.Create(new { SessionTicket = credential }) };
        request.Headers.Add("X-SecretKey", this.secretKey);
        using HttpResponseMessage response = await this.client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using JsonDocument document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("PlayFab response was empty.");
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("IsSessionTicketExpired", out JsonElement expired) && expired.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("UserInfo", out JsonElement user) || !user.TryGetProperty("PlayFabId", out JsonElement id) || string.IsNullOrWhiteSpace(id.GetString())) return null;
        var claims = new Dictionary<string, string>(StringComparer.Ordinal) { ["playfab_id"] = id.GetString()! };
        if (user.TryGetProperty("Username", out JsonElement username) && username.ValueKind == JsonValueKind.String) claims["username"] = username.GetString()!;
        return new ExternalIdentity("playfab", id.GetString()!, claims);
    }
}

/// <summary>Loads non-secret PlayFab settings from a dotenv file or process environment.</summary>
public static class PlayFabEnvironment
{
    public static (string TitleId, string SecretKey, string? SessionTicket) Load(string path = ".env")
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(path)) foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf('='); if (separator <= 0) continue;
            values[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"', '\'');
        }
        string Read(string name) => values.TryGetValue(name, out string? fromFile) && fromFile.Length != 0 ? fromFile : Environment.GetEnvironmentVariable(name) ?? "";
        string titleId = Read("PLAYFAB_TITLE_ID"); string secretKey = Read("PLAYFAB_SECRET_KEY"); string? ticket = Read("PLAYFAB_SESSION_TICKET");
        if (string.IsNullOrWhiteSpace(titleId)) throw new InvalidOperationException("PLAYFAB_TITLE_ID is not configured.");
        if (string.IsNullOrWhiteSpace(secretKey)) throw new InvalidOperationException("PLAYFAB_SECRET_KEY is not configured.");
        return (titleId, secretKey, string.IsNullOrWhiteSpace(ticket) ? null : ticket);
    }
}
