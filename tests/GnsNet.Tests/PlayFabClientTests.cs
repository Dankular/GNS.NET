namespace GnsNet.Tests;

using GnsNet;
using System.Net;
using System.Text;
using Xunit;

public sealed class PlayFabClientTests
{
    [Fact]
    public async Task RegisterAndLogin_ReturnAccountAndEntitySessions()
    {
        var handler = new QueueHandler(
            "{\"data\":{\"PlayFabId\":\"PF-1\",\"SessionTicket\":\"ticket-1\",\"EntityToken\":{\"EntityToken\":\"entity-1\",\"Entity\":{\"Id\":\"PF-1\",\"Type\":\"title_player_account\"}}}}",
            "{\"data\":{\"PlayFabId\":\"PF-1\",\"SessionTicket\":\"ticket-2\"}}",
            "{\"data\":{\"EntityToken\":\"entity-2\",\"Entity\":{\"Id\":\"PF-1\",\"Type\":\"title_player_account\"}}}");
        var client = new PlayFabRestClient("ABCD1", new HttpClient(handler));
        PlayFabAccountSession registered = await client.RegisterAsync("dev@example.com", "password", "developer");
        PlayFabAccountSession loggedIn = await client.LoginAsync("developer", "password");
        PlayFabEntitySession entity = await client.GetEntityTokenAsync(loggedIn.SessionTicket);
        PlayFabEntitySession ensured = await client.EnsureEntitySessionAsync(registered);
        Assert.Equal("PF-1", registered.PlayFabId); Assert.Equal("entity-1", registered.EntitySession?.EntityToken);
        Assert.Equal("ticket-2", loggedIn.SessionTicket); Assert.Equal("entity-2", entity.EntityToken); Assert.Equal("entity-1", ensured.EntityToken);
        Assert.Equal("X-Authorization", handler.Requests[2].Headers.First().Key);
    }

    [Fact]
    public async Task ServerAndLobbyOperationsUseEntityTokensAndMapResults()
    {
        var handler = new QueueHandler(
            "{\"data\":{\"EntityToken\":\"title-entity\",\"Entity\":{\"Id\":\"title\",\"Type\":\"title\"}}}",
            "{\"data\":{\"EntityToken\":\"server-entity\",\"Entity\":{\"Id\":\"server-1\",\"Type\":\"game_server\"}}}",
            "{\"data\":{\"LobbyId\":\"lobby-1\",\"ConnectionString\":\"join-1\"}}",
            "{\"data\":{\"LobbyId\":\"lobby-1\"}}");
        var client = new PlayFabRestClient("ABCD1", new HttpClient(handler));
        PlayFabEntitySession server = await client.RegisterServerAsync(new string('s', 32), "secret");
        PlayFabLobby created = await client.CreateServerLobbyAsync(server, new PlayFabLobbyOptions { MaxPlayers = 4 });
        PlayFabLobby joined = await client.JoinLobbyAsServerAsync(server, "join-1");
        Assert.Equal("game_server", server.Entity.Type); Assert.Equal("join-1", created.ConnectionString); Assert.Equal("lobby-1", joined.LobbyId);
        Assert.Equal("X-EntityToken", handler.Requests[1].Headers.First().Key); Assert.Equal("X-EntityToken", handler.Requests[2].Headers.First().Key);
    }

    [Fact]
    public async Task ServerRegistration_AcceptsNestedEntityTokenResponse()
    {
        var handler = new QueueHandler(
            "{\"data\":{\"EntityToken\":\"title-entity\",\"Entity\":{\"Id\":\"title\",\"Type\":\"title\"}}}",
            "{\"data\":{\"EntityToken\":{\"EntityToken\":\"server-entity\",\"Entity\":{\"Id\":\"server-1\",\"Type\":\"game_server\"}}}}");
        var client = new PlayFabRestClient("ABCD1", new HttpClient(handler));
        PlayFabEntitySession server = await client.RegisterServerAsync(new string('s', 32), "secret");
        Assert.Equal("server-entity", server.EntityToken);
        Assert.Equal("server-1", server.Entity.Id);
        Assert.Equal("game_server", server.Entity.Type);
    }

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly string[] responses;
        private int index;
        public List<HttpRequestMessage> Requests { get; } = new();
        public QueueHandler(params string[] responses) => this.responses = responses;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Requests.Add(request); string body = this.responses[this.index++]; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
