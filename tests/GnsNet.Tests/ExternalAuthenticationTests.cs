namespace GnsNet.Tests;

using GnsNet;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http;
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

    [Fact]
    public async Task PlayFabVerifier_ValidatesSessionTicketAndExtractsPlayFabId()
    {
        string? receivedSecret = null; string? receivedBody = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            receivedSecret = request.Headers.GetValues("X-SecretKey").Single(); receivedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"IsSessionTicketExpired\":false,\"UserInfo\":{\"PlayFabId\":\"PF-42\",\"Username\":\"runner\"}}", Encoding.UTF8, "application/json") };
        }));
        var verifier = new PlayFabSessionTicketVerifier("ABCD1", "server-secret", client);
        ExternalIdentity? identity = await verifier.VerifyAsync("ticket-123");
        Assert.Equal("server-secret", receivedSecret); Assert.Contains("ticket-123", receivedBody); Assert.Equal("PF-42", identity?.Subject); Assert.Equal("runner", identity?.Claims["username"]);
    }

    [Fact]
    public async Task PlayFabVerifier_RejectsExpiredTicketAndNonHttpsCustomAdapters()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"IsSessionTicketExpired\":true}") }));
        Assert.Null(await new PlayFabSessionTicketVerifier("ABCD1", "server-secret", client).VerifyAsync("expired"));
        Assert.Throws<ArgumentException>(() => new PlayFabIdentityVerifier(client, new Uri("http://localhost/verify")));
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
