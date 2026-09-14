# GNS.NET benchmarks

`GnsNet.Benchmarks` is a dependency-light stress harness for the framework hot paths. It reports elapsed time, operations/second, processed bytes, and managed allocation per operation.

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario all --clients 32 --entities 5000 --iterations 2000
```

Scenarios are `serialize`, `stress`, `batch`, `pipeline`, `prediction`, `replay`, `matrix`, `transport`, `p2p`, and `playfab`. `stress` runs concurrent client serialization with `--parallelism` worker limits, useful for finding allocation and contention problems. Adjust `--clients`, `--entities`, `--iterations`, `--payload-bytes`, and `--parallelism` to model a target workload. Use Release builds for comparisons and repeat runs on an otherwise idle machine.

The `playfab` scenario uses one persistent account from `.env`/environment credentials and performs a live adapter smoke test: login, obtain the Entity Session Token, register a game-server entity, create a server-owned Lobby, and join it as the client. It never creates an account during normal runs and never prints credentials. Set `PLAYFAB_TEST_EMAIL`, `PLAYFAB_TEST_USERNAME`, and `PLAYFAB_TEST_PASSWORD` once. To explicitly provision that account the first time, add `--register-test-account` for one run, then omit it afterward:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario playfab --register-test-account
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario playfab
```

The `transport` scenario starts a real local GNS listener and clients, then measures loopback echo throughput, bytes on wire, connection setup, RTT p50/p99, and loss. It is development-only and deliberately disables native authentication/encryption for loopback. Use `--native-path` when the GNS DLL is not next to the benchmark executable:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario transport --clients 32 --iterations 10000 --payload-bytes 256 --native-path C:\path\to\GameNetworkingSockets.dll
```

Without a native GNS library the tool reports transport capacity as unavailable; application-layer scenarios remain runnable.

The `p2p` scenario attempts a local `ListenP2P` socket, obtains the native identity, connects a
second in-process peer with `ConnectP2P`, and records whether echo traffic succeeds. It is
development-only and requires `--insecure` plus `--native-path` when the native library is not
beside the executable:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario p2p --insecure --native-path C:\path\to\GameNetworkingSockets.dll --iterations 100
```

An unavailable/timeout result is a capability result, not a passing traversal test: GNS P2P still
needs the rendezvous/signaling environment. This does not prove NAT traversal, relay selection,
Coturn, IPv4/IPv6, symmetric-NAT, or hostile public-network behavior.

The `matrix` scenario runs a deterministic managed orchestration across connection count, room
readiness, entity count, payload size, impairment loss, reconnect rehydration, and capture replay.
It writes one JSON record per profile and is useful for regression comparisons without native
libraries:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario matrix --clients 32 --entities 5000 --iterations 1000 --payload-bytes 256 --deterministic --json artifacts/matrix.json
```

This matrix does not simulate kernel NAT behavior or replace two-peer IPv4/IPv6, symmetric-NAT,
Coturn, or hostile-network tests.

For repeatable CI comparisons, use `--deterministic`. It removes wall-clock/allocation fields from the JSON and emits a stable SHA-256 replay fingerprint:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario replay --iterations 1000 --deterministic --json artifacts/replay.json
```

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario stress --clients 1000 --iterations 5000 --payload-bytes 256 --parallelism 12
```
