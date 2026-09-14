# GNS.NET benchmarks

`GnsNet.Benchmarks` is a dependency-light stress harness for the framework hot paths. It reports elapsed time, operations/second, processed bytes, and managed allocation per operation.

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario all --clients 32 --entities 5000 --iterations 2000
```

Scenarios are `serialize`, `stress`, `batch`, `pipeline`, `prediction`, `transport`, and `playfab`. `stress` runs concurrent client serialization with `--parallelism` worker limits, useful for finding allocation and contention problems. Adjust `--clients`, `--entities`, `--iterations`, `--payload-bytes`, and `--parallelism` to model a target workload. Use Release builds for comparisons and repeat runs on an otherwise idle machine.

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

For repeatable CI comparisons, use `--deterministic`. It removes wall-clock/allocation fields from the JSON and emits a stable SHA-256 replay fingerprint:

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario replay --iterations 1000 --deterministic --json artifacts/replay.json
```

```powershell
dotnet run --project benchmarks/GnsNet.Benchmarks -c Release -- --scenario stress --clients 1000 --iterations 5000 --payload-bytes 256 --parallelism 12
```
