# GNS.NET execution checklist

This is the execution checklist requested for the milestone gap closure. It is not a replacement for
implementation: an item may be marked `[x]` only after integration, tests, and verification. `[~]`
means a supported partial implementation or an external validation dependency; `[ ]` means work remains.

## M1 — Replication lifecycle

- [~] M1.1 Automatic reconnect object-graph rehydration
  - [x] Persist bounded authoritative lifecycle history per session.
  - [x] Replay spawn, ownership, and despawn records after resumption.
  - [x] Verify ordering and duplicate suppression during replay.
  - [x] Automatically feed registered host/registry lifecycle events into the session journal.
  - [ ] Send a complete initial object graph when the journal retention window is exceeded.
- [~] M1.2 Complete command/RPC framework
  - [ ] Add typed command contracts.
  - [ ] Add targeted client routing.
  - [x] Add request timeout and expiry handling.
  - [ ] Add endpoint capability discovery.
- [ ] M1.3 Room/scene lifecycle integration
  - [ ] Add scene transition records.
  - [ ] Add late-join state transfer integration.
  - [ ] Add full reconnect-during-spawn integration coverage.

## M2 — Replication protocol

- [ ] M2.1 Generated replicated-field encoders
  - [ ] Generate field IDs and stable wire ordering.
  - [ ] Generate encode/decode methods using dirty masks.
  - [ ] Generate schema compatibility metadata.
- [ ] M2.2 Caller-free automatic replication tick
  - [ ] Integrate lifecycle records with AOI/delta/batching.
  - [ ] Automatically schedule dirty components.
  - [ ] Select channel and priority without per-message caller plumbing.
- [ ] M2.3 Automatic budgets and shed accounting
  - [ ] Integrate byte/message budgets into scheduler.
  - [ ] Add queue-age/starvation handling.
  - [ ] Expose per-connection shed/drop decisions.
- [ ] M2.4 Malformed-input property/fuzz suite
  - [ ] Fuzz malformed snapshots and baseline loss.
  - [ ] Fuzz schema mismatch, tick wraparound, and partial entities.

## M3 — Prediction and time

- [ ] M3.1 Integrated predicted world runtime
  - [ ] Store input/state/simulation metadata per tick.
  - [ ] Rewind and resimulate a complete sample world.
  - [ ] Apply correction smoothing to rendered state.
- [ ] M3.2 Clock/tick policy integration
  - [ ] Apply measured drift to synchronization policy.
  - [ ] Integrate bounded catch-up/slow-down into TickLoop.
  - [ ] Expose prediction cost and misprediction magnitude.
- [ ] M3.3 Impairment and long-rollback tests
  - [ ] Test latency, jitter, packet loss, and duplicate snapshots.
  - [ ] Test clock drift and long rollback windows.

## M4 — AOI and lag compensation

- [ ] M4.1 Visibility-safe replication integration
  - [ ] Enforce scene/team/owner/occlusion lifecycle ordering.
  - [ ] Prevent hidden entities from entering serialized batches.
- [ ] M4.2 Bounded hitbox budgets
  - [ ] Add memory/retention budgets and rejection metrics.
- [ ] M4.3 Authoritative shooter rewind integration
  - [ ] Bind measured per-client view time to rewind queries.
  - [ ] Test hidden-entity and unauthorized-time attacks.

## M5 — Native and P2P operations

- [ ] M5.1 Network matrix
  - [ ] Test LAN, NAT types, IPv4/IPv6, and symmetric-connect paths.
  - [ ] Test relay fallback and hostile-network impairment.
- [~] M5.2 Native transport verification
  - [x] Linux Docker native build and loopback verification.
  - [ ] Windows native runtime verification.
  - [ ] Authenticated native CI client/server run.
- [ ] M5.3 Native CI artifacts
  - [ ] Cache native dependencies.
  - [ ] Publish and consume Linux/Windows artifacts in CI.

## M6 — Developer experience and operations

- [ ] M6.1 Generated docs/templates
  - [ ] Generate API/reference documentation.
  - [ ] Add server, client, room, entity, and auth-adapter templates.
- [ ] M6.2 Observability
  - [ ] Add OpenTelemetry dashboards.
  - [ ] Add player-facing overlay integration.
- [ ] M6.3 Replay and load testing
  - [ ] Add persistent replay index and metadata.
  - [ ] Add redaction and deterministic playback environments.
  - [ ] Add CI replay regression captures.
  - [ ] Add connection, room, entity, payload, impairment, and reconnect-storm ramps.
- [ ] M6.4 Release compatibility
  - [ ] Define compatibility and migration policy.
  - [ ] Add migration tooling, package signing, and release smoke tests.
