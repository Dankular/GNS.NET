namespace GnsNet.Tests;

using GnsNet;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class ExternalAuthenticationTests
{
    [Fact]
    public async Task JwtVerifier_ValidatesSignatureAndClaims()
    {
        using RSA signingKey = RSA.Create(2048);
        string token = CreateToken(signingKey, "key-1", "https://issuer.test", "game", "player-1", DateTimeOffset.UtcNow.AddMinutes(2));
        var verifier = new JwtIdentityVerifier("oidc", "https://issuer.test", "game", new Dictionary<string, RSA> { ["key-1"] = signingKey });
        ExternalIdentity? identity = await verifier.VerifyAsync(token);
        Assert.Equal("oidc", identity?.Provider); Assert.Equal("player-1", identity?.Subject);
    }

    [Fact]
    public async Task JwtVerifier_RejectsTamperingAndWrongAudience()
    {
        using RSA signingKey = RSA.Create(2048);
        string token = CreateToken(signingKey, "key-1", "https://issuer.test", "wrong-game", "player-1", DateTimeOffset.UtcNow.AddMinutes(2));
        var verifier = new JwtIdentityVerifier("oidc", "https://issuer.test", "game", new Dictionary<string, RSA> { ["key-1"] = signingKey });
        Assert.Null(await verifier.VerifyAsync(token));
        string[] parts = token.Split('.'); string tampered = parts[0] + "." + Base64Url(JsonSerializer.Serialize(new { iss = "https://issuer.test", aud = "game", sub = "attacker", exp = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds() })) + "." + parts[2];
        Assert.Null(await verifier.VerifyAsync(tampered));
    }

    [Fact]
    public async Task AuthenticationGateway_ExchangesVerifiedIdentityForAdmissionToken()
    {
        var gateway = new AuthenticationGateway(new ConnectTokenService(new byte[32]));
        string? token = await gateway.AuthenticateAsync(new FixedVerifier(), "credential", TimeSpan.FromMinutes(1));
        Assert.NotNull(token);
        Assert.False(new ConnectTokenService(Enumerable.Repeat((byte)1, 32).ToArray()).TryValidate(token!, out _)); // token secrets are never interchangeable
    }

    private sealed class FixedVerifier : IExternalIdentityVerifier
    {
        public ValueTask<ExternalIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken = default)
            => new(new ExternalIdentity("test", "player-1", new Dictionary<string, string>()));
    }

    private static string CreateToken(RSA key, string kid, string issuer, string audience, string subject, DateTimeOffset expires)
    {
        string header = Base64Url(JsonSerializer.Serialize(new { alg = "RS256", kid, typ = "JWT" }));
        string payload = Base64Url(JsonSerializer.Serialize(new { iss = issuer, aud = audience, sub = subject, exp = expires.ToUnixTimeSeconds() }));
        byte[] signature = key.SignData(Encoding.ASCII.GetBytes(header + "." + payload), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return header + "." + payload + "." + Base64Url(signature);
    }
    private static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
