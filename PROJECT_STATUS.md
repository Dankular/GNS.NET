# GNS.NET project status and delivery plan

This is the single authoritative execution checklist, roadmap, and gap audit.

## Status contract

- `[x]` = implemented, integrated, tested, and verified at the stated scope.
- `[~]` = closest supported implementation exists, but an external/backend/platform dependency remains.
- `[ ]` = implementation or required verification remains.
- `interface-only` = an API, wrapper, or harness exists without proving production behavior.

## Master checklist

### M1 — Replication lifecycle

- [x] Reconnect object-graph rehydration with bounded lifecycle journal, ordered replay, duplicate
  suppression, automatic host lifecycle capture, and full-graph fallback.
- [x] Typed MemoryPack command/RPC contracts, targeted routing, authority, correlation, timeout,
  capability discovery, and server/client host integration.
- [x] Room membership/readiness/start locking, late join transfer, scene transitions, and reconnect
  lifecycle integration.

### M2 — Replication protocol

- [x] Generated replicated-field IDs, stable wire order, dirty-mask codecs, schema metadata, and
  compatibility rejection.
- [x] Automatic tick scheduling for AOI, delta baselines, relevance priority, channel selection,
  batching, lifecycle records, and component dirty tracking.
- [x] Byte/message budgets, queue age/starvation policy, and shed/drop metrics.
- [x] Deterministic malformed corpus plus bounded seeded fuzz families for schema, wraparound,
  partial entities, and baseline loss.

### M3 — Prediction and time

- [~] Tick metadata, bounded rollback/resimulation, correction smoothing, and misprediction metrics
  are implemented and tested; complete gameplay-world integration remains application-owned.
- [x] Drift-aware clock/tick policy, bounded catch-up/slow-down, and prediction cost metrics.
- [x] Latency, jitter, loss, duplicate snapshot, drift, and long-rollback tests.

### M4 — AOI and lag compensation

- [x] Spatial AOI distance, scene, team, owner, visibility, and occlusion predicates.
- [x] Automatic lifecycle observer refresh using filtered spatial AOI, with reliable spawn/despawn
  ordering and hidden-entity wire tests.
- [x] Bounded hitbox history, retention/rejection metrics, ray/sphere/box queries, sub-tick
  interpolation, measured view time, and target authorization.
- [~] Full gameplay-level shooter integration remains application-owned.

### M5 — Native, P2P, and operations

- [x] Native status, lanes, queue/congestion metrics, impairment configuration, and platform smoke
  probes (Linux and Windows x64 artifacts/runtime, IPv4/IPv6 loopback, and native loss injection).
- [~] Native ICE/STUN/TURN configuration injection, short-lived credential rotation, signaling, direct-
  to-relay fallback, and the repository two-peer/soak harness are integrated and CI-configured; public
  traversal remains unverified because it requires two external peers.
- [~] Authenticated native scenario and protected certificate CI path are integrated; actual CI
  execution remains unprovisioned until `GNS_NATIVE_CERTIFICATE_B64` contains a valid coordinator
  certificate.
- [~] Local P2P identity/loopback probe and signaling client exist; production rendezvous remains
  application/service-owned.
- [ ] Two-external-peer LAN, IPv4/IPv6, NAT-type, symmetric-NAT, relay, and hostile-network matrix.

### M6 — Developer experience and release operations

- [x] Generated API docs, templates, metrics/dashboard, player overlay, indexed replay, redaction,
  deterministic fingerprints, and CI replay regression.
- [x] Managed connection/room/entity/payload/impairment/reconnect matrix, native saturation/soak
  coverage, and cross-dimension migration batching are implemented and tested.
- [~] Compatibility policy, migration manifest validation, package/consumer smoke, and protected
  package-signing execution path exist; coordinated conversion and trusted certificate provisioning
  remain operational dependencies.

## Current evidence

Replication v2 delivery evidence:

- Explicit-ID semantic generator, generated descriptors/codecs, and generator round-trip tests are present.
- Monotonic `NetworkClock`, bounded v2 packet codec, lifecycle scheduler, observer budgets, and
  acknowledgement-only baselines are present and covered by deterministic core tests.
- The optional `GnsNet.Stride` adapter and headless adapter tests are included in the solution. The
  locally available Stride 4.3 package targets `net10.0`; the core remains `net9.0` and has no Stride
  dependency.
- `GnsNet.ReplicationManifest` validates checked-in evolution and can verify or emit generated metadata.

- `dotnet build GnsNet.sln -c Release --no-restore`: 0 warnings, 0 errors.
- `dotnet test tests/GnsNet.Tests/GnsNet.Tests.csproj -c Release --no-restore`: 177 passed, 1 expected
  Schannel/TLS platform skip.
- `./scripts/test-release-operations.ps1`: release migration and signing-readiness checks passed.
- Build VPS: GS-managed Coturn 4.6.3 was running; authenticated allocation exchanged 20/20 packets,
  completed channel binds, and reported 0% loss. The GNS.NET native container was rebuilt from the
  reviewed GNS commit `a424b7db649438acafb60c99cae6667587c42732` and ran the managed suite plus the
  native loopback harness. The pinned image completed 200/200 IPv6 loopback echoes at 0% loss and
  134/200 unique IPv4 sequence echoes with configured 10% native loss (33% observed in this short
  impairment sample), with 0 duplicates and 66 missing sequences.
  The default compose value is pinned to that same commit; the container build emitted only upstream
  CMake warnings about unused GNS build options. The benchmark now embeds and validates application
  sequence numbers, reporting unique, duplicate, and missing native messages for loss measurement.
- Managed protocol fuzzing now includes 3,000 seeded frame/batch round trips and mutation checks;
  AOI lifecycle coverage includes independent multi-client scene/team views; Steam auth callback state
  and ownership handling are unit-tested.
- Hosted CI: Linux/Windows native artifact production, downloaded runtime smoke, and SHA-256 identity
  checks have passed on terminal runs. Native CI now enables the upstream P2P/soak tests and a 16-client
  saturation benchmark. Workflow concurrency cancels superseded pushes and keeps the latest continuous
  trigger authoritative.
- Without a coordinator certificate, the authenticated native probe returns `CannotTry`; this is an
  explicit unprovisioned capability result, not an authentication pass.
- Without a release certificate, package signing reports SDK support but leaves the package unsigned.

## Open limitations

Real NAT traversal with two external peers; full public Coturn/VPS peer validation; coordinator-issued
native certificate provisioning in CI; Steamworks runtime/client validation for `BeginAuthSession`;
authenticated native client/server CI with real certificates; full gameplay-world AOI/lag-compensation
integration; broader fuzzing; game-specific replay/load orchestration; coordinated production data
conversion; and trusted package certificate provisioning/signing remain open. Windows native loopback
is CI-verified; only secure certificate-backed execution remains conditional.

## Ownership and references

GS owns identity, matchmaking, allocation, match lifecycle, join claims, and the shared VPS Coturn
deployment. GNS.NET owns realtime transport, admission, sessions, replication, prediction/rewind,
metrics, and replay. See [`README.md`](README.md), [`docs/gameservice-integration.md`](docs/gameservice-integration.md),
and [`docs/turn-relay.md`](docs/turn-relay.md).

Canonical checks:

```powershell
dotnet build GnsNet.sln -c Release --no-restore
dotnet test tests/GnsNet.Tests/GnsNet.Tests.csproj -c Release --no-build
./scripts/release-smoke.ps1
./scripts/validate-migration.ps1 -CurrentManifest .\current-release.json -CandidateManifest .\candidate-release.json
```
