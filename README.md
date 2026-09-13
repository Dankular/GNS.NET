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

The framework leaves game-specific state and physics to the application, but provides the transport
policies and integration points around them. MemoryPack messages must be schema-defined with
`[MemoryPackable]`; server input handlers registered through `GnsServerHost` always pass through
the registered guards before application handlers run.

`GnsServerHost` requires a `ConnectionAdmission` by default. Construct it with a
`ConnectTokenService` backed by a web backend or matchmaker-issued short-lived token; set
`RequireApplicationAdmission = false` only for an intentionally open development server.

## Opinionated framework API

Applications can use `GnsAuthoritativeServer<TSessionId, TState, TInput>` and
`GnsPredictedClient<TInput, TState>` when they want the framework to compose the common systems:

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

The test suite exercises serialization, framing, prediction/interpolation, validation, session
resumption, auth, metrics, replay persistence, adaptive load shedding, TCP backend messaging, and
shard migration without requiring a native GNS server. Running `samples/Poc` end-to-end additionally
needs the native library described above.

## License

MIT (see [LICENSE](LICENSE)). `GnsNet` depends on GnsSharp (MIT) and, transitively at runtime,
on the native GameNetworkingSockets library (BSD-3-Clause) or the Steamworks SDK, depending on
which backend you build against.
