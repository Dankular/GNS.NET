# GNS.NET

A reusable authoritative-server networking layer for C# game projects, built on
[GnsSharp](https://github.com/nalchi-net/GnsSharp)'s binding of Valve's
[GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets) (GNS).

This repository started as a single-purpose proof of concept and now provides a reusable framework
layer in [`GnsNet`](src/GnsNet): MemoryPack framing, authoritative simulation helpers, prediction /
reconciliation, snapshot interpolation, resumable sessions, authenticated admission, validation,
AOI/delta/priority snapshot delivery, diagnostics, replay, scaling, backend/shard coordination,
native ICE/STUN/TURN configuration, and certificate-aware transport policy. High-level frames carry
both protocol and MemoryPack schema revisions and reject unsupported revisions before deserialization.

The managed framework is suitable for beginning game integration. It is not a claim that this
repository alone is production-ready for public-Internet deployment: real two-peer NAT traversal,
Steamworks runtime ticket validation, certificate-provisioned authenticated native CI, public-network matrix
testing, and the release-operation items listed in [PROJECT_STATUS.md](PROJECT_STATUS.md) still
require external systems or platform credentials. The GS-managed Coturn deployment has been
validated on the build VPS; the missing proof is the two-peer path through it.

## Companion GameService

GNS.NET is the realtime game-networking layer in the companion
[GameService](https://github.com/Dankular/GS) platform. GameService owns Nakama identity,
matchmaking, Agones allocation, match state, and short-lived Ed25519-signed join claims.
GNS.NET owns the allocated server's gameplay transport: connection admission, authoritative
simulation, replication, prediction, reconnect grace, AOI, lag compensation, metrics, and replay.

The intended production flow is:

`Nakama session → matchmaking ticket → Agones allocation → GameService join claim → game-server claim validation → GNS.NET admission token → realtime connection`

The join claim is not sent as an arbitrary UDP credential. The game server validates its
match, allocation, build, roster, slot, and expiry constraints first, then issues a separate
short-lived GNS.NET connection token. See [GameService integration](docs/gameservice-integration.md)
for the boundary and example flow. The GameService repository is the source of truth for
control-plane APIs; this repository is the source of truth for realtime gameplay APIs.

## Walkthrough: GameService (GS) and GNS.NET together

Think of GS as the control plane and GNS.NET as the data plane:

```text
client
  │ Nakama login / matchmaking
  ▼
GS control plane ── allocates a server, checks roster and build, signs a join claim
  │ HTTPS join claim
  ▼
allocated game server ── verifies the GS claim and creates a short-lived transport token
  │ GameNetworkingSockets connection
  ▼
GNS.NET host ── admits the connection, runs ticks, validates input, and replicates state
```

The two systems are joined at the allocated game-server boundary. GS does not serialize gameplay
snapshots and GNS.NET does not own Nakama sessions, matchmaking, Agones, or the match database.
The normal match flow is:

1. The client authenticates with Nakama and obtains GS matchmaking/session credentials.
2. GS matches the player and allocates a game server. The allocation supplies the server address,
   ports, match identity, and a short-lived signed join claim.
3. The client presents that claim to the allocated game server over the GS join API. The game
   server verifies the Ed25519 signature and checks match, allocation, player, roster, build, slot,
   and expiry before allowing realtime access.
4. The game server maps the verified GS subject to a GNS.NET session id and issues a separate,
   short-lived `ConnectTokenService` token. This token is scoped to realtime transport; it is not
   the GS signing claim and is never minted by an untrusted client.
5. The client connects to the allocated address with `GnsClient`/`GnsClientHost`. GNS.NET validates
   the admission token in its reserved handshake frame, then the server attaches the session and
   starts accepting typed input.
6. After admission, GNS.NET owns the authoritative tick loop, input guards, AOI/delta snapshots,
   prediction/reconciliation metadata, reconnect grace, heartbeat metrics, and replay capture.
   GS remains responsible for match lifecycle and service-level health decisions.

### Game-server integration

This is the GNS.NET side of the boundary. `VerifyGsJoinClaimAsync` is the small GS adapter: its
implementation belongs to GS or the game server because it needs the GS Ed25519 public key and
match/allocation state.

```csharp
using GnsNet;

using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions
{
    RequireNativeAuthentication = true,
});
using GnsServer server = GnsServer.Listen("[::]:27015");

byte[] admissionSecret = Convert.FromBase64String(
    Environment.GetEnvironmentVariable("GNS_ADMISSION_SECRET")
        ?? throw new InvalidOperationException("GNS_ADMISSION_SECRET is required."));
var tokenService = new ConnectTokenService(admissionSecret);
var admission = new ConnectionAdmission(tokenService);
var host = new GnsServerHost<string>(server, TimeSpan.FromSeconds(30), admission);

// Called by the GS join endpoint handler, before attaching a realtime session.
async Task<string> AdmitGsJoinAsync(string signedGsClaim, CancellationToken cancellationToken)
{
    GsJoinClaim claim = await VerifyGsJoinClaimAsync(signedGsClaim, cancellationToken);
    // The adapter must reject bad signatures, wrong match/allocation/build,
    // unknown roster members, duplicate slots, and expired claims.
    return tokenService.Issue(claim.Subject, TimeSpan.FromSeconds(30));
}

// The returned token is supplied by the client during the GNS.NET handshake.
bool accepted = host.Admit(connection, transportToken);
if (accepted)
    host.AttachSession(claim.Subject, connection);
```

In a real server, `connection`, `transportToken`, and `claim` are values from the server's
join/session coordinator rather than global variables. The security ordering is:

```text
verify GS claim → issue GNS.NET token → establish GNS connection → host.Admit → AttachSession
```

The client receives only the allocated endpoint and short-lived transport token:

```csharp
using GnsRuntime runtime = GnsRuntime.Initialize();
var reconnecting = new ReconnectableClient(() => GnsClient.Connect(allocatedAddress));
var clientHost = new GnsClientHost(reconnecting, transportToken);
clientHost.Start();
```

### Where GameNetworkingSockets fits

GNS.NET is the managed framework layer; Valve's GameNetworkingSockets is the native transport
engine loaded by GnsSharp. `GnsRuntime` loads one native library, initializes GNS, and pumps its
callback queue. `GnsServer`/`GnsClient` create native listen/connect handles. `GnsServerHost` and
`GnsClientHost` add the game protocol above those handles: versioned `NetFrame` messages, MemoryPack
payloads, admission, heartbeats, channels, replication, and reconnect behavior.

```text
Game state / simulation / GS claim adapter
                 │
        GnsServerHost / GnsClientHost
                 │  NetFrame + MemoryPack
        GnsServer / GnsClient (GnsSharp)
                 │  P/Invoke
        GameNetworkingSockets native library
                 │
       UDP / relay / ICE / native encryption
```

GNS provides delivery, connection state, channels, congestion handling, encryption/certificate
state, and (when built and deployed for it) ICE/SDR relay behavior. GNS.NET provides the
authoritative game protocol and policy around that transport. Native GNS certificate provisioning
and GS join-claim validation are separate checks. For local development, use the explicit
`--insecure` sample switch only; public deployments must use native authentication/encryption and
a protected GS admission secret.

For a runnable two-process example, see [`samples/Poc/README.md`](samples/Poc/README.md). For the
actual GS HTTP boundary and claim-validation responsibilities, see
[`docs/gameservice-integration.md`](docs/gameservice-integration.md).

The audited capability roadmap and comparison against Unity Netcode, Photon Fusion, FishNet,
Mirror, Unreal Iris, Godot, and Valve GNS is in [PROJECT_STATUS.md](PROJECT_STATUS.md). It distinguishes
implemented and tested framework behavior from external validation and remaining work.

## Capability status

The following is the current repository audit. “Implemented and verified” means the behavior is
integrated into the library and covered by automated tests or a checked-in local harness. An
interface, adapter, configuration surface, or standalone benchmark is not counted as a completed
production deployment feature.

| Area | Status | Boundary |
|---|---|---|
| Framing, schema revisions, validation, auth admission, reconnect grace, room lifecycle, AOI/delta/priority delivery, replay persistence, and managed replication tests | Implemented and verified | Game-specific state, physics, and predicate functions remain application-owned; the lifecycle scheduler integrates supplied scene/team/owner/visibility/occlusion predicates. |
| Prediction, interpolation, deterministic impairment, bounded rewind queries, lifecycle cleanup, and malformed-input corpus testing | Implemented and verified | A complete gameplay shooter integration and broader property-based fuzzing remain open. |
| Native TURN/STUN configuration and direct-then-relay signaling orchestration | Implemented and verified at the managed/configuration boundary | The GS-managed Coturn service is deployed and allocation-validated on the build VPS; actual GNS NAT/relay traversal needs two external peers. |
| Linux native loopback and Windows x64 native loopback | Implemented and CI-verified | The checked-in development smoke path is explicitly insecure; secure native execution is conditional on a protected certificate. |
| Managed connection/room/entity/payload/impairment/reconnect/replay matrix | Implemented and CI-verified | Native full-scale storm, LAN, IPv4/IPv6, NAT-type, symmetric-NAT, relay, and hostile-network coverage remain open. |
| Heartbeat RTT and sequence-based loss accounting | Implemented, integrated, tested, and VPS-verified | The managed heartbeat and native benchmark use explicit sequence observations; GNS quality-derived native telemetry remains available separately. |
| Replay capture, persistence, redaction, deterministic playback, and regression fingerprints | Implemented and CI-verified | Full replay/load orchestration across every connection, room, payload, impairment, and reconnect-storm dimension remains open. |
| Telemetry meters, Grafana dashboard, renderer-neutral player overlay, and filtered AOI lifecycle scheduling | Implemented and tested | Game-specific observability and gameplay policy remain application work. |
| Native certificate-aware transport policy and coordinator certificate installation | Implemented and tested | Authenticated native CI executes only when a valid coordinator-issued certificate is provisioned. |
| Steam `BeginAuthSession` ticket callback lifecycle | Callback-owning manager implemented and unit-tested | End-to-end Steam client/game-server validation still requires the Steamworks SDK runtime, which this package deliberately does not initialize. |
| Migration validation and package signing | Implemented as policy/capability tooling | Coordinated data conversion and trusted package certificate provisioning are operational release responsibilities. |

Still-open external or integration work includes: real NAT traversal with two external peers;
Steamworks runtime/client validation for `BeginAuthSession`; authenticated native client/server CI with a provisioned
real certificate; LAN,
IPv4/IPv6, symmetric-NAT, relay, and hostile-network matrices; broader scene/team/owner AOI
lifecycle tests; full gameplay-level lag compensation; broader property-based fuzzing; native
full replay/load orchestration; coordinated migration/data
conversion tooling; and trusted package certificate provisioning/signing in CI. These are listed
explicitly so a successful managed build is not mistaken for completion of those external systems.

The authoritative execution checklist and precise external limitations are consolidated in
[PROJECT_STATUS.md](PROJECT_STATUS.md); the legacy [TASKS.md](TASKS.md), [TODO.md](TODO.md), and
[MILESTONES.md](MILESTONES.md) files point to it. The project is
ready for real game integration, but these open boundaries prevent a claim of public-Internet
production readiness.

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
| `LifecycleReplicationScheduler` | Automatic observer enter/leave lifecycle delivery for authoritative spawn, despawn, and ownership records. |
| `ReliableBackendBus` / `TcpBackendMessageBus` | Authenticated, ordered, retrying server-to-server transport. |
| `NetworkDebugOverlay` | Renderer-neutral per-connection RTT, loss, packet, and byte diagnostics for in-game overlays. |
| `HttpP2PSignalingClient` / `P2PTraversalTester` | Authenticated rendezvous-plan exchange and direct-then-relay probe orchestration. |
| `GnsP2POptions` | Native ICE/STUN settings plus aligned short-lived TURN server/user/password lists. |
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
An importable Grafana dashboard for the built-in OpenTelemetry meters is in [`docs/observability/gnsnet-grafana-dashboard.json`](docs/observability/gnsnet-grafana-dashboard.json).
The compatibility and migration contract is documented in [`docs/compatibility.md`](docs/compatibility.md).
The generated public API reference is in [`docs/api-reference.md`](docs/api-reference.md); regenerate it with `scripts/generate-api-reference.ps1`.

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
For authoritative object lifecycle, `LifecycleReplicationScheduler` refreshes observer membership
automatically and emits reliable spawn/despawn records in visibility order. Scene, team, owner, and
occlusion decisions remain application-defined predicates supplied to the AOI provider.

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

Heartbeat probes carry sequence numbers and monotonic send timestamps. Acknowledgements update a
smoothed RTT and a bounded loss window; duplicate, late, and still-in-flight probes are not counted
as loss. Native connection status and queue metrics are available separately through
`NativeConnectionStatistics`.

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
`AuthoritativeRewindService<TClientId>` composes measured per-client view time, bounded hitbox
history, fractional-tick interpolation, and target authorization for authoritative 2D rewind
queries. `HitboxRewindHistory` remains available for lower-level composition. `GnsTelemetry` emits
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
relay fallback paths. Pass the returned endpoints as `GnsP2POptions.TurnRelays` to configure the
native GNS TURN server/user/password lists:

```csharp
P2PTraversalPlan plan = await signaling.PollAsync(sessionId, peerId);
var p2p = new GnsP2POptions
{
    IceCandidatePolicy = 0x7fffffff,
    StunServerList = "stun:stun.example.com:3478",
    TurnRelays = plan.Relays
};
p2p.Validate();
```

The signaling service must never expose long-lived TURN shared secrets to clients. Native config
injection and the GS-managed Coturn allocation path are implemented and tested; actual GNS relay
selection still needs two external peers outside the VPS network.

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
this framework enforces. `SteamAuthSessionManager` owns the `BeginAuthSession` call, retains the
`ValidateAuthTicketResponse` callback, and records accepted/rejected results when an `ISteamUser`
runtime is available. The selected `GnsNet` build still initializes the open-source GNS backend,
not the Steamworks SDK runtime, so the Steam client/game-server path remains an external validation.
The framework includes `ShardSupervisor` for detecting and
restarting dead local shard processes; deployment systems may still provide an outer supervisor for
host-machine failures. The managed P2P/ICE entry points and native TURN credential configuration
are present; use two external peers and a deployed relay to validate actual traversal.
See [`PROJECT_STATUS.md`](PROJECT_STATUS.md).

### Native GNS certificates and game join claims

The open-source GnsSharp binding exposes native certificate provisioning, so a coordinator can
issue a SteamDatagram certificate from a certificate request and install it before authentication:

```csharp
using GnsSharp;

var sockets = ISteamNetworkingSockets.User!;
byte[] request = NativeAuthentication.CreateCertificateRequest(sockets);
// Send request to the trusted game coordinator. Keep the returned certificate secret.
byte[] certificate = await coordinator.IssueCertificateAsync(request);
NativeAuthentication.SetCertificate(sockets, certificate);
var availability = NativeAuthentication.GetStatus(sockets, out var status);
```

`GnsRuntimeOptions.NativeCertificate` performs the same `SetCertificate` operation during runtime
initialization, before `InitAuthentication` and before creating listen/connect sockets. The blob
must come from a secret store or coordinator response; no certificate material belongs in source,
CI variables committed to the repository, or client logs. Native certificate validation establishes
the authenticated/encrypted GNS transport identity. It does not replace the application-level GS
join claim: the GS control plane still issues a short-lived signed claim containing the match,
allocation, player, build, and expiry, and `ConnectionAdmission` validates that claim after the
transport connection is established.

`NativeAuthentication.Capabilities.SteamAuthTickets` remains `false` for the selected open-source
GNS authentication capability. The separate `SteamAuthSessionManager` is the callback integration
surface, but it requires the Steamworks GnsSharp backend and Steamworks runtime for an end-to-end
ticket validation.

When the hosting process supplies an `ISteamUser` implementation, the Steam ticket lifecycle is:

```csharp
using GnsSharp;

using var steamAuth = new SteamAuthSessionManager();
steamAuth.ValidationReceived += validation =>
{
    if (!validation.Accepted) { /* reject the game-session admission */ return; }
    // Map validation.SteamId and validation.OwnerSteamId into the GS join-claim policy.
};

EBeginAuthSessionResult started = steamAuth.BeginAuthSession(ticketBytes, playerSteamId);
if (started != EBeginAuthSessionResult.OK) { /* reject immediately */ }
// Keep the manager alive until ValidationReceived; call EndAuthSession(playerSteamId) on leave.
```

The immediate return value only starts validation; identity acceptance comes from the asynchronous
`ValidateAuthTicketResponse` callback. This callback manager is integrated and unit-tested, while
actual Steam account/server validation remains dependent on Steamworks runtime initialization and
valid platform credentials.

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
src/GnsNet.Generators/ Source generators for schema and replication metadata.
samples/Poc/           The original POC, rebuilt on top of GnsNet - a server, a client, and
                        their shared protocol/message definitions.
tests/GnsNet.Tests/    Managed unit and integration tests for framing, lifecycle, prediction,
                        authentication, AOI, replay, backend messaging, and transport policy.
benchmarks/             JSON-capable serialization, replication, replay, load, and transport probes.
native/GameNetworkingSockets/
                        Pinned native source tree used by the Docker/VPS and CI build harnesses.
docker/                 Coturn and native-runtime verification configurations.
docs/                   API, integration, compatibility, relay, and operations documentation.
```

## Building and testing

```powershell
dotnet restore
dotnet build GnsNet.sln -c Release --no-restore
dotnet test GnsNet.sln -c Release --no-build
```

The benchmark tool can emit CI-friendly JSON alongside its human-readable score:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario all --json artifacts/gnsnet-benchmark.json
```

For coordinated managed load coverage across connections, room readiness, entities, payload sizes,
impairment, reconnect rehydration, and replay, run the deterministic matrix scenario:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario matrix --clients 32 --entities 5000 --iterations 1000 --payload-bytes 256 --deterministic --json artifacts/matrix.json
```

This is managed regression coverage; it does not replace public IPv4/IPv6, NAT-type, symmetric-NAT,
Coturn, or hostile-network testing.

The native Linux harness builds GameNetworkingSockets, runs the managed suite, and executes the
explicitly insecure local transport smoke test:

```powershell
docker compose -f docker/docker-compose.native.yml build --pull
docker compose -f docker/docker-compose.native.yml run --rm gnsnet-native-ci
```

The harness proves Linux native loading, loopback setup, and managed contracts. It does not prove
public NAT traversal, Steamworks authentication, or a certificate-authenticated client/server run.
The GS-managed Coturn deployment and authenticated allocation have been separately validated on
the build VPS. Windows x64 loopback has also been verified in hosted CI with the downloaded DLL;
the authenticated native CI probe is conditional on `GNS_NATIVE_CERTIFICATE_B64`. Keep
`--insecure` limited to local development and isolated loopback validation.

To attempt local native P2P API wiring with two in-process peers, run the separately gated smoke
benchmark. Its JSON result explicitly records success or unavailability:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario p2p --insecure --native-path <path-to-GameNetworkingSockets> --iterations 100
```

An unavailable result means the required rendezvous/signaling path is absent; this is not evidence
of public NAT traversal or relay operation.

The test suite exercises serialization, framing, prediction/interpolation, validation, session
resumption, auth, metrics, replay persistence, adaptive load shedding, TCP backend messaging, and
shard migration without requiring a native GNS server. Running `samples/Poc` end-to-end additionally
needs the native library described above.

## Release and contribution checks

Before merging a transport or protocol change, run the Release build and full test suite, then
regenerate the API reference when public XML documentation changes:

```powershell
dotnet build GnsNet.sln -c Release --no-restore
dotnet test GnsNet.sln -c Release --no-build
./scripts/generate-api-reference.ps1
git diff --check
```

The hosted workflow runs on every branch push and pull request, repeats the deterministic managed
matrix and replay fingerprint checks, executes Linux and Windows native artifact/runtime probes,
and performs a scheduled daily verification. Native authenticated CI runs when
`GNS_NATIVE_CERTIFICATE_B64` is provisioned; the package workflow similarly requires protected
signing secrets. VPS allocation evidence is recorded in [`docs/turn-relay.md`](docs/turn-relay.md),
while public two-peer matrix runs remain separate external validation.

## License

MIT (see [LICENSE](LICENSE)). `GnsNet` depends on GnsSharp (MIT) and, transitively at runtime,
on the native GameNetworkingSockets library (BSD-3-Clause). This repository selects the open-source
GNS backend; Steamworks SDK runtime initialization and end-to-end ticket validation are not included.
