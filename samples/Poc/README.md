# GNS.NET authoritative-server POC

The original proof of concept this repository grew out of: a 20Hz authoritative server
holding one entity's position, a client sending scripted directional input, and unreliable
tick-numbered state broadcast/reconciliation. Built and run as two separate processes.

Where the original POC hand-rolled everything in each `Program.cs`, this version is built on
the reusable [`GnsNet`](../../src/GnsNet) library in this repository - see the root
[README](../../README.md) for what that library provides and why it exists.

## Prerequisites
- .NET 9 SDK
- A built `libGameNetworkingSockets.so` (or `.dll` on Windows) - see
  [`ValveSoftware/GameNetworkingSockets`](https://github.com/ValveSoftware/GameNetworkingSockets)'s
  `BUILDING.md`. `-DENABLE_ICE=OFF` is enough for a LAN/loopback POC like this one, and is
  what this repository's own end-to-end test was built with. Commit
  [`725e273`](https://github.com/ValveSoftware/GameNetworkingSockets/tree/725e273c7442bac7a8bc903c0b210b1c15c34d92)
  is the version [GnsSharp](https://github.com/nalchi-net/GnsSharp) (the P/Invoke binding
  `GnsNet` wraps) currently targets.
- Point `GnsNet` at your build: either drop the native library next to each sample's build
  output, or set the `GNSNET_NATIVE_LIBRARY_PATH` environment variable - see
  `NativeLibraryLoader` in `src/GnsNet`.

## Run
Terminal 1: `dotnet run --project samples/Poc/Poc.Server`
Terminal 2 (after the server prints "Listening..."): `dotnet run --project samples/Poc/Poc.Client`

The client's input isn't real keyboard/controller input - it's a deterministic scripted
pattern (`ScriptedInput` in `Poc.Shared`) that cycles right / down / left / up every 2 seconds,
so a run is reproducible without a human at the keyboard.

## Protocol (intentionally minimal - not production framing)
Defined once, in `Poc.Shared/Protocol.cs`, and used by both ends:
- Client -> Server: `[0x01][sbyte dx][sbyte dy]`, sent `UnreliableNoDelay` every 50ms.
- Server -> Client: `[0x02][uint32 tick][float x][float y]`, sent `UnreliableNoDelay` every
  tick (50ms). The client discards any state packet whose tick isn't newer than the last one
  it applied (`TickSequence.IsNewer`, from `GnsNet`).

The bytes themselves are packed with `GnsNet`'s `PacketWriter`/`PacketReader` (big-endian),
rather than by hand with `BitConverter` in each `Program.cs` - but the wire format and framing
choices are exactly as minimal as the original POC's.

## What this does NOT cover
- Client-side prediction or interpolation (the client just displays raw server state).
- Reconnect/timeout handling, lag compensation, delta compression.
- Authentication, or any protection against a malicious client.
- Anything resembling a real serialization format - this is still hand-packed bytes, just
  through a shared helper instead of duplicated inline.

`GnsServer` (the library type this sample's server is built on) does support multiple
simultaneous client connections via a poll group - unlike the original POC, that part isn't a
limitation of this version. This sample still only demonstrates one client driving one shared
entity, though; it doesn't spawn a per-client entity or otherwise show multi-client gameplay.
