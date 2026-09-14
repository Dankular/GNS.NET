# GNS.NET

A reusable authoritative-server networking layer for C# game projects, built on
[GnsSharp](https://github.com/nalchi-net/GnsSharp)'s binding of Valve's
[GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets) (GNS).

This repository started as a single-purpose proof of concept and now provides a reusable framework
layer in [`GnsNet`](src/GnsNet): MemoryPack framing, authoritative simulation helpers, prediction /
reconciliation, snapshot interpolation, resumable sessions, authenticated admission, validation,
AOI/delta/priority snapshot delivery, diagnostics, replay, scaling, and backend/shard coordination.
High-level frames carry both protocol and MemoryPack schema revisions and reject unsupported revisions
before deserialization.

The forward-looking capability roadmap and comparison against Unity Netcode, Photon Fusion, FishNet,
Mirror, Unreal Iris, Godot, and Valve GNS is in [MILESTONES.md](MILESTONES.md). It separates the
implemented primitives from the remaining higher-level replication runtime work.

## What's in `GnsNet`

| Type | What it's for |
|---|---|
| `GnsRuntime` | Process-wide GNS lifecycle: loads the native library, calls `GameNetworkingSockets.Init`/`Kill`, and pumps `RunCallbacks()` on a background loop. Create exactly one per process. |
| `GnsServer` | A listen socket that accepts any number of client connections onto one poll group; send to one connection, or broadcast to all of them. |
| `GnsClient` | A single outbound connection to a `GnsServer`. |
| `PacketWriter` / `PacketReader` | A small growable-buffer, big-endian byte writer/reader for hand-packed wire messages - not a serialization framework, just the manual byte-packing every project building on raw GNS ends up writing anyway, done once. |
| `TickLoop` | Drives a fixed-interval loop (a server simulation tick, or a client's periodic input send). |
| `TickSequence` | Wraparound-safe "is this tick newer than that one" comparison for a `uint` tick counter (RFC 1982 serial number arithmetic). |
| `NativeLibraryLoader` | Resolves and loads the native `GameNetworkingSockets` shared library (explicit path, `GNSNET_NATIVE_LIBRARY_PATH` env var, or platform default next to the executable). |
| `GnsServerHost` / `GnsClientHost` | Integrated framework hosts for authentication, heartbeats, reconnect/session grace, typed routing, batching, metrics, replay, and channel policy. |
| `SnapshotPipeline` | Automatic AOI culling, acknowledged delta baselines, relevance priority, batching, and state/event channels. |
| `ReliableBackendBus` / `TcpBackendMessageBus` | Authenticated, ordered, retrying server-to-server transport. |
| `NetworkDebugOverlay` | Renderer-neutral per-connection RTT, loss, packet, and byte diagnostics for in-game overlays. |
| `FlatFileIdentityVerifier` | Built-in PBKDF2-backed JSON user authentication for local and small deployments. |

The framework leaves game-specific state and physics to the application, but provides the transport
policies and integration points around them. MemoryPack messages must be schema-defined with
`[MemoryPackable]`; server input handlers registered through `GnsServerHost` always pass through
the registered guards before application handlers run.

`GnsServerHost` requires a `ConnectionAdmission` by default. Construct it with a
`ConnectTokenService` backed by a web backend or matchmaker-issued short-lived token; set
`RequireApplicationAdmission = false` only for an intentionally open development server.

### External authentication adapters

GNS.NET does not require a Steamworks identity provider. Verify a provider credential in your web
backend, then exchange the verified identity for a short-lived GNS.NET ticket:

```csharp
var gateway = new AuthenticationGateway(connectTokenService);
var verifier = new JwtIdentityVerifier("oidc", issuer, audience, jwksKeys);
string? ticket = await gateway.AuthenticateAsync(verifier, providerJwt, TimeSpan.FromMinutes(2));
// Send `ticket` to the client; GnsClientHost sends it in the admission handshake.
```

`JwtIdentityVerifier` validates RS256 signatures, `kid`, issuer, audience, `exp`, and `nbf` claims.
Use `JwksLoader.LoadAsync` to load RSA keys from an HTTPS JWKS endpoint. For providers whose native
credential format must be checked by their SDK or service, use the HTTPS validation adapters:

```csharp
using var http = new HttpClient();
var eos = new EosIdentityVerifier(http, new Uri("https://auth.example.com/verify/eos"));
var playFab = new PlayFabIdentityVerifier(http, new Uri("https://auth.example.com/verify/playfab"));
var xbox = new XboxXstsIdentityVerifier(http, new Uri("https://auth.example.com/verify/xsts"));
var google = new GooglePlayGamesIdentityVerifier(http, new Uri("https://auth.example.com/verify/google"));
var apple = new AppleGameCenterIdentityVerifier(http, new Uri("https://auth.example.com/verify/apple"));
```

Each endpoint must authenticate the credential with the provider, return HTTP 2xx, and respond with
`{"subject":"provider-user-id","claims":{...}}`. The adapters reject non-HTTPS endpoints and
never treat an unverified client-supplied subject as an identity. This supports EOS, PlayFab,
Xbox/XSTS, Google Play Games, Apple Game Center, and any custom provider without adding their SDKs
to the transport package.

#### EOS Connect adapter

`EosConnectIdentityVerifier` is the EOS-specific contract. The configured HTTPS service must use
the EOS SDK/service to validate the Connect credential, then return the verified Product User ID
and context. EOS Connect supports external credentials and produces Product User IDs for crossplay;
the game should obtain the credential through EOS Connect, while the validation service owns EOS
credentials and provider configuration. [Epic's EOS documentation](https://dev.epicgames.com/documentation/unreal-engine/online-subsystem-eos-plugin-in-unreal-engine)

```csharp
var eos = new EosConnectIdentityVerifier(
    httpClient,
    new Uri("https://auth.example.com/verify/eos-connect"),
    new EosConnectValidationOptions
    {
        DeploymentId = environment.EosDeploymentId,
        SandboxId = environment.EosSandboxId,
        ClientId = environment.EosClientId,
        Nonce = loginNonce
    });

string? ticket = await gateway.AuthenticateAsync(
    eos, eosConnectCredential, TimeSpan.FromMinutes(2));
```

The verifier sends the credential and expected context to the service. The service response must be
similar to:

```json
{
  "valid": true,
  "productUserId": "puid-example",
  "deploymentId": "deployment-example",
  "sandboxId": "sandbox-example",
  "clientId": "client-example",
  "nonce": "login-nonce",
  "expiresAt": "2030-01-01T00:00:00Z"
}
```

GNS.NET rejects the response when `valid` is false, the Product User ID is absent, any configured
deployment/sandbox/client/nonce does not match, or `expiresAt` has passed. The EOS client secret
and EOS SDK remain backend-only; they are never sent over the GNS transport.

#### Built-in flat-file authentication

For local tools, private test servers, and small deployments that do not need an external identity
provider, `FlatFileIdentityVerifier` reads PBKDF2 password records from a JSON file. Generate users
in an administrative setup tool; do not hand-write or store plaintext passwords:

```csharp
FlatFileUser admin = FlatFilePasswordHasher.CreateUser(
    "developer", "correct horse battery staple", "developer-1",
    new Dictionary<string, string> { ["role"] = "admin" });
await File.WriteAllTextAsync("users.json", JsonSerializer.Serialize(new[] { admin }));

var verifier = new FlatFileIdentityVerifier("users.json");
string credential = new FlatFileCredential("developer", password).Encode();
string? ticket = await gateway.AuthenticateAsync(verifier, credential, TimeSpan.FromMinutes(15));
```

The file contains `Username`, `Subject`, `PasswordHash`, `Salt`, `Iterations`, `Disabled`, and
optional `Claims`. Password verification uses PBKDF2-HMAC-SHA256 with a per-user random salt and a
minimum iteration count. This provider is deliberately intended for development/small deployments;
use PlayFab, EOS, or an OIDC provider when accounts, rotation, MFA, and audit requirements belong in
a managed identity system.

For a direct PlayFab integration, use `PlayFabSessionTicketVerifier`. It calls PlayFab's
`https://<titleId>.playfabapi.com/Server/AuthenticateSessionTicket` endpoint with `X-SecretKey`
server-side, then maps the returned `UserInfo.PlayFabId` into a GNS.NET admission ticket. The POC
also supports `--playfab`; see [`samples/Poc/README.md`](samples/Poc/README.md#run). Keep the title
secret in `.env` or a secret manager—never in a client build, command history, or Git.

#### PlayFab account, server, and Lobby flow

`PlayFabRestClient` provides the account and Lobby REST flow without requiring the PlayFab SDK:

```csharp
var playFab = new PlayFabRestClient(titleId);

// Client account flow. Keep returned credentials in memory.
PlayFabAccountSession account = await playFab.RegisterAsync(
    "player@example.com", password, "player-name");
// Existing accounts use:
// PlayFabAccountSession account = await playFab.LoginAsync("player-name", password);

PlayFabEntitySession playerEntity = await playFab.EnsureEntitySessionAsync(account);

// Register a dedicated server once with a stable 32-100 character ID.
PlayFabEntitySession serverEntity = await playFab.RegisterServerAsync(
    serverInstanceId, titleSecretKey);

PlayFabLobby lobby = await playFab.CreateServerLobbyAsync(serverEntity,
    new PlayFabLobbyOptions { MaxPlayers = 16, AccessPolicy = "Public" });
PlayFabLobby joined = await playFab.JoinLobbyAsync(
    playerEntity, lobby.ConnectionString!);
```

`RegisterServerAsync` uses the title secret only on the server to obtain a title entity token and
register a `game_server` entity. Lobby calls use `X-EntityToken`; clients join with
`JoinLobbyAsync`, while a game server can attach to a client-owned lobby with
`JoinLobbyAsServerAsync`. PlayFab requires an authenticated entity before Lobby operations and
supports both client-owned and server-owned lobbies. See [Create Lobby](https://learn.microsoft.com/en-us/rest/api/playfab/multiplayer/lobby/create-lobby)
and [Join Lobby](https://learn.microsoft.com/en-us/rest/api/playfab/multiplayer/lobby/join-lobby).

Do not pass `PlayFabAccountSession.SessionTicket` directly to a Lobby method. Session tickets are
for legacy Client/Server authentication; Lobby requires the entity token returned by
`Authentication/GetEntityToken` and sends it as `X-EntityToken`.

For a local two-process smoke test where Steamworks/native auth-ticket provisioning is unavailable,
the POC supports an explicit development-only `--insecure` flag:

```powershell
dotnet run --project samples/Poc/Poc.Server -- --insecure --duration-seconds 15
dotnet run --project samples/Poc/Poc.Client -- 127.0.0.1:27015 --insecure --duration-seconds 10
```

Both processes must receive the flag. It disables native authentication/encryption checks and
skips native authentication initialization; it is not a replacement for production auth and must
never be enabled on an internet-facing server.

## Opinionated framework API

Applications can use `GnsAuthoritativeServer<TSessionId, TState, TInput>` and
`GnsPredictedClient<TInput, TState>` when they want the framework to compose the common systems:

Copyable server/client/contract/auth starting points are in [`templates/`](templates/README.md).

```csharp
var server = new GnsAuthoritativeServer<string, WorldState, PlayerInput>(
    gnsServer, initialWorld,
    (state, player, input) => state.Apply(player, input),
    new ServerInputGuard<string, PlayerInput>((player, input) => input.IsValid),
    sessionGracePeriod: TimeSpan.FromSeconds(30));

server.PollAndAdvance();
server.BroadcastState();
```

The client facade predicts local input immediately, reconciles authoritative MemoryPack states,
and provides delayed snapshot interpolation for rendering:

```csharp
var client = new GnsPredictedClient<PlayerInput, WorldState>(
    clientHost, initialWorld,
    (state, input) => state.ApplyLocal(input),
    (from, to, amount) => WorldState.Lerp(from, to, amount));

client.SubmitInput(localTick, input);
client.Poll();
if (client.TryRender(serverTick, out var renderState)) Draw(renderState);
```

`NetworkFrameworkOptions` controls application opcodes, snapshot buffering, and interpolation
delay. Lower-level hosts and pipelines remain available for custom AOI, delta, priority, replay,
backend, sharding, and transport policies.

### Define wire messages with MemoryPack

Every application message should be schema-defined. `NetFrame` adds the protocol/schema version,
tick, and opcode around the MemoryPack payload:

```csharp
using MemoryPack;

[MemoryPackable]
public partial class PlayerInput
{
    public float MoveX { get; set; }
    public float MoveY { get; set; }
}

[MemoryPackable]
public partial class WorldState
{
    public int ServerTick { get; set; }
    public Dictionary<string, Vector2> Players { get; set; } = new();
}
```

### Authenticated admission and session resumption

Issue the short-lived token from a web backend or matchmaker. The server host consumes it from the
reserved handshake frame before dispatching gameplay frames; reconnecting with the same session id
can resume the grace-period slot:

```csharp
var tokenService = new ConnectTokenService(RandomNumberGenerator.GetBytes(32));
string token = tokenService.Issue("player-42", TimeSpan.FromMinutes(2));
var admission = new ConnectionAdmission(tokenService);
var host = new GnsServerHost<string>(server, TimeSpan.FromSeconds(30), admission);

// The client sends the token in its handshake automatically when supplied to GnsClientHost.
var reconnecting = new ReconnectableClient(() => GnsClient.Connect("127.0.0.1:27015"));
var clientHost = new GnsClientHost(reconnecting, token);
clientHost.Start();
```

Use `GnsDisconnectKind.GracefulQuit` to remove state immediately. Transport loss and heartbeat
timeouts detach the connection while preserving the session until the grace period expires.

### Typed reliable and unreliable traffic

Use unreliable state messages for frequently changing values and reliable event messages for data
that must arrive exactly once at the application layer:

```csharp
const byte ChatOpcode = 10;
const byte InputOpcode = 11;

// Reliable and ordered by GNS: chat, inventory, match events.
clientHost.Send(ChatOpcode, tick, new ChatMessage { Text = "ready" }, NetChannel.Event.SendType());

// Unreliable state: a newer input/snapshot supersedes an older one.
clientHost.Send(InputOpcode, tick, new PlayerInput { MoveX = 1 }, NetChannel.State.SendType());

clientHost.Router.Register<ChatMessage>(ChatOpcode, (message, frame) => chat.Add(message.Text));
```

`NetBatch` combines multiple frames into one packet. `GnsServerHost.SendBatch` and
`GnsClientHost.SendBatch` preserve the selected channel for the whole batch.

### AOI, delta compression, priority, and batching

For a custom snapshot policy, compose the lower-level pipeline once and drain it each server tick:

```csharp
var interest = new InterestManager<string, Entity>();
interest.SetView("player-42", new InterestPoint(x: 0, y: 0, radius: 50));

var delta = new DeltaCompressor<WorldState>(
    (baseline, current) => WorldStateDelta.Create(baseline, current),
    (baseline, change) => WorldStateDelta.Apply(baseline, change));
var snapshots = new SnapshotPipeline<string, Entity, WorldState>(
    interest, delta, entity => (entity.X, entity.Y));

snapshots.Queue("player-42", world.Entities, world.State,
    entityOpcode: 20, snapshotOpcode: 21, tick, relevance: 1.0f);
var batch = new NetBatch();
foreach (var item in snapshots.Drain("player-42", maxFrames: 64)) batch.Add(item.Frame);
if (batch.Count > 0) host.SendBatch(connection, batch, NetChannel.State.SendType());
```

Lower relevance values automatically reduce update frequency. `PrioritySendQueue` can be used when
an application needs to mix event, nearby-state, and distant-state priorities in the same tick.

### Prediction and interpolation without the facade

The state helpers are also usable independently of a transport host:

```csharp
var prediction = new ClientPrediction<PlayerInput, WorldState>();
prediction.Add(inputTick, input);
WorldState corrected = prediction.Reconcile(
    acknowledgedTick, serverState,
    (state, pendingInput) => state.ApplyLocal(pendingInput));

var buffer = new SnapshotBuffer<WorldState>(capacity: 32);
buffer.Add(serverTick, corrected);
if (buffer.TrySample(renderTick,
    (from, to, amount) => WorldState.Lerp(from, to, amount), out var smoothState))
    Draw(smoothState);
```

### Diagnostics, impairment, and replay

Attach these services to a host during development to reproduce poor network conditions and capture
desyncs. Heartbeat probes update RTT and loss metrics automatically:

```csharp
var recorder = new NetworkRecorder();
var conditions = new NetworkConditionSimulator(
    new NetworkConditions(TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(20), 3), seed: 7);
var diagnosticsHost = new GnsServerHost<string>(
    server, TimeSpan.FromSeconds(30), admission)
{
    Conditions = conditions,
    Recorder = recorder
};

// Render this string in an in-game debug overlay.
string overlay = NetworkDebugOverlay.Format(
    NetworkDebugOverlay.Snapshot("player-42", diagnosticsHost.Metrics));

await recorder.SaveAsync("captures/desync.gnsr");
var capture = await NetworkRecorder.LoadAsync("captures/desync.gnsr");
await capture.ReplayTransportAsync(packet => ReplayToTestServer(packet));
```

### Backend messaging and sharding

Keep matchmaker, persistence, and game processes off the player-facing socket. The TCP backend bus
provides an authenticated process boundary; `ReliableBackendBus` adds retries and per-topic order:

```csharp
await using var backend = await TcpBackendMessageBus.ConnectAsync("backend.internal", 4100);
await using var reliable = new ReliableBackendBus(backend, backendSecret);
await reliable.PublishAsync(new BackendMessage(
    "persistence", MemoryPackSerializer.Serialize(playerSave), DateTimeOffset.UtcNow));
```

Route matches/rooms to independent processes and coordinate lifecycle and migration through the
sharding APIs:

```csharp
var directory = new ShardDirectory<string>();
directory.Add("match-process-a");
directory.Add("match-process-b");
string shard = directory.Select("match-123");

var lifecycle = new ShardLifecycle<string, string>(TimeSpan.FromMinutes(1));
var coordinator = new ShardProcessCoordinator<string, string>(processController, lifecycle, transfer);
await coordinator.MigrateAsync("player-42", shard);
```

### Replication runtime primitives

The framework also exposes engine-neutral lifecycle, room, RPC, prediction, AOI, rewind, and
transport operations. These are designed to be composed by a game adapter rather than hidden in
the transport:

```csharp
var objects = new NetworkObjectRegistry<string>();
NetworkObjectDescriptor player = objects.Spawn(typeId: 7, owner: "player-42", tick: 10);
objects.TransferOwnership(player.ObjectId, "server");

var room = new RoomLifecycle<string>(maxPlayers: 16) { AllowLateJoin = true };
room.Join("player-42"); room.SetReady("player-42", true); room.Start(); room.BeginGame();

var ticks = new TickRateCoordinator(serverHz: 60);
ticks.ApplyServerClock(serverTick: 120, serverHz: 60);
int catchUp = ticks.TicksToSimulate(localTick: 117);

var lanes = connection.ConfigureLanes([0, 10], [1, 1]);
NativeConnectionStatistics native = connection.GetStatistics();
```

For high-volume snapshots, `ParallelSnapshotEncoder` shares immutable encoded results across
connections and `SpatialHashInterestManager` supports distance plus scene/team/visibility rules.
`HitboxRewindHistory` provides bounded authoritative 2D rewind queries. `GnsTelemetry` emits
OpenTelemetry-compatible activities and meters for bytes, drops, RTT, and pending reliable data.

Before accepting gameplay messages, negotiate the wire contract and keep RPC replies correlated:

```csharp
var protocol = new ProtocolCompatibility(new ProtocolVersion(1, 3, 4));
ProtocolNegotiationResult negotiated = protocol.Negotiate(remoteVersion);
if (!negotiated.Accepted) return; // close before dispatching gameplay frames

var requests = new RpcRequestTracker();
var pending = requests.Create(cancellationToken);
Send(new RpcRequest(pending.RequestId, "inventory.use", null, playerId, payload, tick));
// On the response path: requests.Complete(response). Unknown/duplicate IDs are ignored.
RpcResponse response = await pending.Completion;
```

The scheduler owns the per-client AOI/delta/priority pass and can publish one authoritative
world for every registered client:

```csharp
var scheduler = new AutomaticSnapshotScheduler<string, Entity, WorldSnapshot>(pipeline);
scheduler.AddClient(playerId);
scheduler.Publish(world, id => BuildSnapshot(id), id => 1f, entityOpcode: 10,
    snapshotOpcode: 11, tick: serverTick);
```

P2P deployments can use `HttpP2PSignalingClient` with a backend-issued bearer token to exchange
short-lived signals and TURN credentials, then use `P2PTraversalTester` in CI to verify direct and
relay fallback paths. The signaling service must never expose long-lived TURN secrets to clients.

## Prerequisites

- .NET 9 SDK
- A native `GameNetworkingSockets` build for your platform (GnsSharp does not ship one - see
  [`samples/Poc/README.md`](samples/Poc/README.md#prerequisites) for how this repository built
  and tested against one).

## Backend selection

GnsSharp ships one NuGet package per backend/platform/bitness combination, because the native
struct layout differs per platform, and the Steamworks SDK backend isn't API-compatible with
the open-source GNS backend at the native level - exactly one package can be referenced by a
given build of `GnsNet.csproj`. This wrapper targets the **open-source GNS backend** (matching
the original POC's prerequisite of a self-built `libGameNetworkingSockets`), and lets you pick
the platform via an MSBuild property:

```powershell
dotnet build -p:GnsNetBackend=Posix64   # default - Linux/POSIX 64-bit
dotnet build -p:GnsNetBackend=Posix32
dotnet build -p:GnsNetBackend=Win64
dotnet build -p:GnsNetBackend=Win32
```

The Steamworks SDK backend isn't wired up here; adding it would mean branching the runtime
init/shutdown path (`SteamAPI.InitEx`/`Shutdown` instead of `GameNetworkingSockets.Init`/`Kill`)
on top of what `GnsRuntime` does today.

The selected open-source backend exposes native authenticated transport/certificate state, which
this framework enforces. Steam `BeginAuthSession` ticket callbacks are a Steamworks API flow and
are not available through this backend. The framework includes `ShardSupervisor` for detecting and
restarting dead local shard processes; deployment systems may still provide an outer supervisor for
host-machine failures. The managed P2P/ICE entry points are present, but the pinned GnsSharp/GNS
native commit documents broken P2P support; use an updated native GNS build to validate traversal.
See [`TODO.md`](TODO.md).

For a reproducible external native runtime, use the checked-in Docker harness described in
[`docs/native-runtime-container.md`](docs/native-runtime-container.md). It builds GNS and runs the
managed suite plus the native loopback benchmark in one isolated environment.

## Using `GnsNet` in a new game project

Reference `src/GnsNet/GnsNet.csproj` (or, once published, a `GnsNet` package) from your game's
project, then:

```csharp
using GnsRuntime runtime = GnsRuntime.Initialize(); // once per process

// Server
using GnsServer server = GnsServer.Listen("[::]:27015");
server.ClientConnected += conn => { /* ... */ };
// each tick:
foreach (ReceivedMessage msg in server.Poll()) { /* read msg.Data, dispatch on your own opcode */ }
server.Broadcast(bytes, ESteamNetworkingSendType.UnreliableNoDelay);

// Client
using GnsClient client = GnsClient.Connect("127.0.0.1:27015");
client.Connected += () => { /* start sending input */ };
client.Send(bytes, ESteamNetworkingSendType.UnreliableNoDelay);
foreach (ReceivedMessage msg in client.Poll()) { /* ... */ }
```

`samples/Poc` is a complete, runnable example of exactly this pattern: see its
[README](samples/Poc/README.md) for the wire protocol it uses and how to run it.

## Repository layout

```
src/GnsNet/            The wrapper library.
samples/Poc/           The original POC, rebuilt on top of GnsNet - a server, a client, and
                        their shared protocol/message definitions.
tests/GnsNet.Tests/    Unit tests for GnsNet's pure logic (packet framing, tick sequencing).
                        These don't touch the native library or the network.
```

## Building and testing

```powershell
dotnet build
dotnet test
```

The benchmark tool can emit CI-friendly JSON alongside its human-readable score:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario all --json artifacts/gnsnet-benchmark.json
```

The test suite exercises serialization, framing, prediction/interpolation, validation, session
resumption, auth, metrics, replay persistence, adaptive load shedding, TCP backend messaging, and
shard migration without requiring a native GNS server. Running `samples/Poc` end-to-end additionally
needs the native library described above.

## License

MIT (see [LICENSE](LICENSE)). `GnsNet` depends on GnsSharp (MIT) and, transitively at runtime,
on the native GameNetworkingSockets library (BSD-3-Clause) or the Steamworks SDK, depending on
which backend you build against.
