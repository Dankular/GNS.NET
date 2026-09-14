# GNS.NET execution checklist

This is the execution checklist requested for the milestone gap closure. It is not a replacement for
implementation: an item may be marked `[x]` only after integration, tests, and verification. `[~]`
means a supported partial implementation or an external validation dependency; `[ ]` means work remains.

## M1 — Replication lifecycle

- [x] M1.1 Automatic reconnect object-graph rehydration
  - [x] Persist bounded authoritative lifecycle history per session.
  - [x] Replay spawn, ownership, and despawn records after resumption.
  - [x] Verify ordering and duplicate suppression during replay.
  - [x] Automatically feed registered host/registry lifecycle events into the session journal.
  - [x] Send a complete initial object graph when the journal retention window is exceeded.
- [x] M1.2 Complete command/RPC framework
  - [x] Add typed MemoryPack command contracts.
  - [x] Add targeted client routing for server-originated RPC invocations and state delivery.
  - [x] Add request timeout and expiry handling.
  - [x] Add endpoint capability discovery.
- [x] M1.3 Room/scene lifecycle integration
  - [x] Add scene transition records.
  - [x] Add late-join state transfer integration.
  - [x] Add full reconnect-during-spawn integration coverage.
  - [x] Integrate room membership/readiness/scene events with authoritative object snapshots for late joiners.
  - [x] Integrate typed RPC request/response envelopes with server and client hosts.

## M2 — Replication protocol

- [x] M2.1 Generated replicated-field encoders
  - [x] Generate field IDs and stable wire ordering.
  - [x] Generate type-specific property encode/decode methods using dirty masks (custom callback overload remains available for special codecs).
  - [x] Generate schema compatibility metadata.
- [x] M2.2 Caller-free automatic replication tick
  - [x] Integrate lifecycle records with AOI/delta/batching.
  - [x] Automatically schedule dirty components through per-component fingerprints during snapshot ticks.
  - [x] Select channel and priority without per-message caller plumbing.
- [x] M2.3 Automatic budgets and shed accounting
  - [x] Integrate byte/message budgets into scheduler.
  - [x] Add queue-age/starvation handling.
  - [x] Expose per-connection shed/drop decisions.
- [ ] M2.4 Malformed-input property/fuzz suite
  - [x] Fuzz malformed snapshots and baseline loss.
  - [x] Exercise schema mismatch, tick wraparound, partial entities, and baseline loss with deterministic property-style corpus tests and bounded rejection assertions.

## M3 — Prediction and time

- [~] M3.1 Integrated predicted world runtime
  - [x] Store input/state/simulation metadata per tick.
  - [ ] Store explicit timestep, deterministic seed, and world-version metadata per tick.
  - [x] Rewind and resimulate a complete sample world.
  - [x] Apply correction smoothing to rendered state.
- [x] M3.2 Clock/tick policy integration
  - [x] Apply measured drift to synchronization policy.
  - [x] Integrate bounded catch-up/slow-down into TickLoop.
  - [x] Expose prediction cost and misprediction magnitude.
- [x] M3.3 Impairment and long-rollback tests
  - [x] Test latency, jitter, packet loss, and duplicate snapshots.
  - [x] Test clock drift and long rollback windows.

## M4 — AOI and lag compensation

- [x] M4.1 Visibility-safe replication integration
  - [x] Enforce scene/team/owner/occlusion lifecycle ordering.
  - [x] Prevent hidden entities from entering serialized batches.
- [ ] M4.2 Bounded hitbox budgets
  - [x] Add memory/retention budgets and rejection metrics.
- [ ] M4.3 Authoritative shooter rewind integration
  - [x] Bind measured per-client view time to rewind queries.
  - [x] Test hidden-entity and unauthorized-time attacks.

## M5 — Native and P2P operations

- [ ] M5.1 Network matrix
  - [ ] Test LAN, NAT types, IPv4/IPv6, and symmetric-connect paths.
  - [ ] Test relay fallback and hostile-network impairment.
- [~] M5.2 Native transport verification
  - [x] Linux Docker native build and loopback verification.
  - [ ] Windows native runtime verification.
  - [ ] Authenticated native CI client/server run.
  - [x] Explicitly gate the development loopback harness behind `--insecure` and publish a no-secrets auth capability report.
- [ ] M5.3 Native CI artifacts
  - [x] Cache native dependencies.
  - [x] Publish and consume Linux/Windows artifacts in CI.
  - [x] Validate x64 format and SHA-256 identity after artifact production and download.

## M6 — Developer experience and operations

- [x] M6.1 Generated docs/templates
  - [x] Generate API/reference documentation from public XML docs.
  - [x] Add server, client, room, entity, and auth-adapter templates.
- [ ] M6.2 Observability
  - [x] Add OpenTelemetry dashboards.
  - [x] Add player-facing overlay integration.
- [ ] M6.3 Replay and load testing
  - [x] Add persistent replay index and metadata.
  - [x] Add redaction and deterministic playback environments (deterministic replay fingerprint and CI verification are integrated; isolated environment orchestration remains open).
  - [x] Add CI replay regression captures.
  - [~] Add connection, room, entity, payload, impairment, and reconnect-storm ramps (CI now runs a high-cardinality smoke profile; a full matrix remains open).
- [ ] M6.4 Release compatibility
  - [x] Define compatibility and migration policy.
  - [~] Add migration tooling, package signing, and release smoke tests (package/consumer smoke is automated; migration conversion and signing remain open).
