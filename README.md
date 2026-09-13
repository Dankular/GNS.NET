# GNS.NET

A reusable authoritative-server networking layer for C# game projects, built on
[GnsSharp](https://github.com/nalchi-net/GnsSharp)'s binding of Valve's
[GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets) (GNS).

This repository started as a single-purpose proof of concept (one entity, one client, scripted
input - see [`samples/Poc`](samples/Poc)). The code here generalizes that into a small library,
[`GnsNet`](src/GnsNet), meant to be the starting point for future game projects rather than
something each new project re-derives from scratch.

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

`GnsNet` is intentionally thin - it does not impose a serialization format, an entity model, or
a reconciliation/prediction scheme on you. It exists to remove the boilerplate around GNS
itself (native lib loading, connection lifecycle, poll groups, message pump/release) that's the
same in every project, so a new game's networking code can start from "define your messages and
your tick loop" rather than from GNS's raw C-ish API.

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

`dotnet test` only exercises the parts of `GnsNet` that don't need the native GNS library
(`PacketWriter`/`PacketReader`/`TickSequence`). Running `samples/Poc` end-to-end additionally
needs the native library described above.

## License

MIT (see [LICENSE](LICENSE)). `GnsNet` depends on GnsSharp (MIT) and, transitively at runtime,
on the native GameNetworkingSockets library (BSD-3-Clause) or the Steamworks SDK, depending on
which backend you build against.
