namespace GnsNet;

using System.Net.Http.Json;
using System.Text.Json;

public readonly record struct PlayFabEntityKey(string Id, string Type);
public readonly record struct PlayFabEntitySession(string EntityToken, PlayFabEntityKey Entity, DateTimeOffset? TokenExpiration = null);
public readonly record struct PlayFabAccountSession(string PlayFabId, string SessionTicket, PlayFabEntitySession? EntitySession);
public readonly record struct PlayFabLobby(string LobbyId, string? ConnectionString);

public sealed class PlayFabLobbyOptions
{
    public int MaxPlayers { get; init; } = 8;
    public string AccessPolicy { get; init; } = "Public";
    public string OwnerMigrationPolicy { get; init; } = "Automatic";
    public bool UseConnections { get; init; } = true;
    public Dictionary<string, string>? LobbyData { get; init; }
    public Dictionary<string, string>? SearchData { get; init; }
    public void Validate()
    {
        if (MaxPlayers is < 2 or > 128) throw new ArgumentOutOfRangeException(nameof(MaxPlayers));
        if (AccessPolicy is not ("Public" or "Friends" or "Private")) throw new ArgumentException("AccessPolicy must be Public, Friends, or Private.", nameof(AccessPolicy));
        if (OwnerMigrationPolicy is not ("Automatic" or "Manual" or "None")) throw new ArgumentException("Unsupported owner migration policy.", nameof(OwnerMigrationPolicy));
    }
}

/// <summary>SDK-free PlayFab account, entity, game-server, and Lobby REST client.</summary>
public sealed class PlayFabRestClient
{
    private readonly HttpClient client;
    private readonly string titleId;
    private readonly string baseUrl;
    public PlayFabRestClient(string titleId, HttpClient? client = null)
    {
        if (string.IsNullOrWhiteSpace(titleId) || titleId.Any(char.IsWhiteSpace)) throw new ArgumentException("A PlayFab title id is required.", nameof(titleId));
        this.titleId = titleId; this.client = client ?? new HttpClient(); this.baseUrl = $"https://{titleId}.playfabapi.com/";
    }

    public async Task<PlayFabAccountSession> RegisterAsync(string email, string password, string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(username)) throw new ArgumentException("Email and username are required.");
        if (string.IsNullOrWhiteSpace(password)) throw new ArgumentException("Password is required.", nameof(password));
        using JsonDocument response = await PostAsync("Client/RegisterPlayFabUser", new { TitleId = this.titleId, Email = email, Password = password, Username = username }, null, cancellationToken).ConfigureAwait(false);
        return ParseAccount(response.RootElement);
    }

    public async Task<PlayFabAccountSession> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await PostAsync("Client/LoginWithPlayFab", new { TitleId = this.titleId, Username = username, Password = password }, null, cancellationToken).ConfigureAwait(false);
        return ParseAccount(response.RootElement);
    }

    public async Task<PlayFabEntitySession> GetEntityTokenAsync(string? sessionTicket = null, string? secretKey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionTicket) == string.IsNullOrWhiteSpace(secretKey)) throw new ArgumentException("Provide exactly one of sessionTicket or secretKey.");
        using JsonDocument response = await PostAsync("Authentication/GetEntityToken", new { }, string.IsNullOrWhiteSpace(sessionTicket) ? ("X-SecretKey", secretKey!) : ("X-Authorization", sessionTicket!), cancellationToken).ConfigureAwait(false);
        return ParseEntitySession(response.RootElement);
    }

    /// <summary>Returns the account's entity session, refreshing it from its login ticket when needed.</summary>
    public Task<PlayFabEntitySession> EnsureEntitySessionAsync(PlayFabAccountSession account, CancellationToken cancellationToken = default)
        => account.EntitySession is PlayFabEntitySession existing
            ? Task.FromResult(existing)
            : this.GetEntityTokenAsync(account.SessionTicket, cancellationToken: cancellationToken);

    /// <summary>Registers or retrieves a persistent PlayFab game_server entity for a process.</summary>
    public async Task<PlayFabEntitySession> RegisterServerAsync(string serverCustomId, string titleSecretKey, CancellationToken cancellationToken = default)
    {
        if (serverCustomId.Length is < 32 or > 100) throw new ArgumentException("PlayFab server custom IDs must be 32-100 characters.", nameof(serverCustomId));
        PlayFabEntitySession title = await this.GetEntityTokenAsync(secretKey: titleSecretKey, cancellationToken: cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await PostAsync("GameServerIdentity/AuthenticateGameServerWithCustomId", new { CustomId = serverCustomId }, ("X-EntityToken", title.EntityToken), cancellationToken).ConfigureAwait(false);
        return ParseEntitySession(response.RootElement);
    }

    public async Task<PlayFabLobby> CreateClientLobbyAsync(PlayFabEntitySession player, PlayFabLobbyOptions? options = null, IReadOnlyDictionary<string, string>? memberData = null, CancellationToken cancellationToken = default)
    {
        (options ??= new PlayFabLobbyOptions()).Validate();
        var member = new { MemberEntity = new { Id = player.Entity.Id, Type = player.Entity.Type }, MemberData = memberData };
        using JsonDocument response = await PostAsync("Lobby/CreateLobby", new { MaxPlayers = options.MaxPlayers, Owner = player.Entity, Members = new[] { member }, AccessPolicy = options.AccessPolicy, OwnerMigrationPolicy = options.OwnerMigrationPolicy, UseConnections = options.UseConnections, LobbyData = options.LobbyData, SearchData = options.SearchData }, ("X-EntityToken", player.EntityToken), cancellationToken).ConfigureAwait(false);
        return ParseLobby(response.RootElement);
    }

    public async Task<PlayFabLobby> CreateServerLobbyAsync(PlayFabEntitySession server, PlayFabLobbyOptions? options = null, CancellationToken cancellationToken = default)
    {
        (options ??= new PlayFabLobbyOptions()).Validate();
        using JsonDocument response = await PostAsync("Lobby/CreateLobby", new { MaxPlayers = options.MaxPlayers, Owner = server.Entity, AccessPolicy = options.AccessPolicy, UseConnections = options.UseConnections, LobbyData = options.LobbyData, SearchData = options.SearchData }, ("X-EntityToken", server.EntityToken), cancellationToken).ConfigureAwait(false);
        return ParseLobby(response.RootElement);
    }

    public async Task<PlayFabLobby> JoinLobbyAsync(PlayFabEntitySession player, string connectionString, IReadOnlyDictionary<string, string>? memberData = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A Lobby connection string is required.", nameof(connectionString));
        using JsonDocument response = await PostAsync("Lobby/JoinLobby", new { ConnectionString = connectionString, MemberEntity = player.Entity, MemberData = memberData }, ("X-EntityToken", player.EntityToken), cancellationToken).ConfigureAwait(false);
        return ParseLobby(response.RootElement);
    }

    public async Task<PlayFabLobby> JoinLobbyAsServerAsync(PlayFabEntitySession server, string connectionString, IReadOnlyDictionary<string, string>? serverData = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("A Lobby connection string is required.", nameof(connectionString));
        using JsonDocument response = await PostAsync("Lobby/JoinLobbyAsServer", new { ConnectionString = connectionString, ServerEntity = server.Entity, ServerData = serverData }, ("X-EntityToken", server.EntityToken), cancellationToken).ConfigureAwait(false);
        return ParseLobby(response.RootElement);
    }

    private async Task<JsonDocument> PostAsync(string path, object body, (string Name, string Value)? header, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(this.baseUrl + path)) { Content = JsonContent.Create(body) };
        if (header is not null) request.Headers.Add(header.Value.Name, header.Value.Value);
        using HttpResponseMessage response = await this.client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"PlayFab request '{path}' failed with HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("PlayFab response was empty.");
    }
    private static PlayFabAccountSession ParseAccount(JsonElement root)
    {
        JsonElement data = root.GetProperty("data"); string id = data.GetProperty("PlayFabId").GetString() ?? throw new InvalidDataException("PlayFab response has no player id."); string ticket = data.GetProperty("SessionTicket").GetString() ?? throw new InvalidDataException("PlayFab response has no session ticket.");
        PlayFabEntitySession? entity = data.TryGetProperty("EntityToken", out JsonElement token) && token.ValueKind == JsonValueKind.Object ? ParseEntitySession(token) : null;
        return new PlayFabAccountSession(id, ticket, entity);
    }
    private static PlayFabEntitySession ParseEntitySession(JsonElement root)
    {
        JsonElement data = root.TryGetProperty("data", out JsonElement nested) ? nested : root; string token = data.GetProperty("EntityToken").GetString() ?? throw new InvalidDataException("PlayFab response has no entity token."); JsonElement entity = data.GetProperty("Entity"); DateTimeOffset? expires = data.TryGetProperty("TokenExpiration", out JsonElement expiry) && DateTimeOffset.TryParse(expiry.GetString(), out DateTimeOffset parsed) ? parsed : null; return new PlayFabEntitySession(token, new PlayFabEntityKey(entity.GetProperty("Id").GetString() ?? "", entity.GetProperty("Type").GetString() ?? ""), expires);
    }
    private static PlayFabLobby ParseLobby(JsonElement root)
    {
        JsonElement data = root.TryGetProperty("data", out JsonElement nested) ? nested : root; return new PlayFabLobby(data.GetProperty("LobbyId").GetString() ?? throw new InvalidDataException("PlayFab response has no lobby id."), data.TryGetProperty("ConnectionString", out JsonElement connection) ? connection.GetString() : null);
    }
}
