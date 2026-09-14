namespace GnsNet;

using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed record P2PSignal(string SessionId, string PeerId, string Type, string Payload, DateTimeOffset ExpiresAt);
public sealed record P2PRelayEndpoint(string Url, string? Username, string? Credential, DateTimeOffset ExpiresAt);
public sealed record P2PTraversalPlan(P2PSignal Signal, IReadOnlyList<P2PRelayEndpoint> Relays);

/// <summary>Authenticated HTTP signaling client. The backend owns authorization and TURN credentials.</summary>
public sealed class HttpP2PSignalingClient
{
    private readonly HttpClient http;
    private readonly Uri endpoint;
    private readonly Func<string> bearerToken;
    public HttpP2PSignalingClient(HttpClient http, Uri endpoint, Func<string> bearerToken)
    { this.http = http ?? throw new ArgumentNullException(nameof(http)); this.endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint)); this.bearerToken = bearerToken ?? throw new ArgumentNullException(nameof(bearerToken)); }
    public async Task<P2PTraversalPlan> PublishAsync(P2PSignal signal, CancellationToken cancellationToken = default)
    { return await SendAsync("publish", signal, cancellationToken).ConfigureAwait(false); }
    public async Task<P2PTraversalPlan> PollAsync(string sessionId, string peerId, CancellationToken cancellationToken = default)
    { if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(peerId)) throw new ArgumentException("Session and peer IDs are required."); return await SendAsync("poll", new PollRequest(sessionId, peerId), cancellationToken).ConfigureAwait(false); }
    private async Task<P2PTraversalPlan> SendAsync(string operation, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(this.endpoint, operation)); request.Headers.Authorization = new("Bearer", this.bearerToken()); request.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await this.http.SendAsync(request, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<P2PTraversalPlan>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Signaling backend returned no traversal plan.");
    }
    private sealed record PollRequest(string SessionId, string PeerId);
}

/// <summary>Deterministic traversal result used by CI and game-server readiness checks.</summary>
public sealed record P2PTraversalResult(bool DirectPath, bool RelayPath, TimeSpan Duration, string? FailureReason);

public static class P2PTraversalTester
{
    public static async Task<P2PTraversalResult> TestAsync(Func<CancellationToken, Task<bool>> directProbe, Func<CancellationToken, Task<bool>> relayProbe, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directProbe); ArgumentNullException.ThrowIfNull(relayProbe); if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        DateTimeOffset start = DateTimeOffset.UtcNow; using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutCts.CancelAfter(timeout);
        bool direct = await directProbe(timeoutCts.Token).ConfigureAwait(false); bool relay = direct || await relayProbe(timeoutCts.Token).ConfigureAwait(false);
        return new(direct, relay, DateTimeOffset.UtcNow - start, relay ? null : "Direct and relay traversal probes failed.");
    }
}
