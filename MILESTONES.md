# GNS.NET milestones and gap analysis

This roadmap compares GNS.NET with production networking stacks and turns the gaps into testable milestones. Statuses below are audited against the current repository: `[x]` means implemented, integrated, tested, and verified; `[~]` means a supported partial implementation or an external validation dependency; `[ ]` means genuinely incomplete. The plan is intentionally engine-neutral and does not treat interfaces or scaffolding as finished features.

Current audit: the M1-M6 foundation is implemented in substantial pieces, but no milestone exit criterion is yet fully satisfied. The remaining gaps are called out explicitly below.

## Findings from comparable libraries

| Capability seen in other stacks | Evidence | Current GNS.NET status |
| --- | --- | --- |
| Network-object lifecycle and remote actions | [Mirror Network Manager](https://mirror-networking.gitbook.io/docs/manual/components/network-manager), [Mirror communications](https://mirror-networking.gitbook.io/docs/manual/guides/communications) | `[x]` Object lifecycle, ownership, typed RPC authority, correlation, timeout, host transport wiring, and targeted client invocation exist. |
| Scene, room, and observer lifecycle | [Mirror Room Manager](https://mirror-networking.gitbook.io/docs/manual/components/network-room-manager), [Mirror scene interest management](https://mirror-networking.gitbook.io/docs/manual/interest-management/scene) | `[x]` Room phases, ready/start lock, late join snapshots, scene transitions, and authoritative lifecycle events are integrated and tested. |
| Full rollback prediction loop | [Unity Netcode prediction](https://docs.unity.cn/Packages/com.unity.netcode%401.0/manual/prediction.html) | `[~]` Bounded rollback/resimulation, tick coordination, and correction metrics exist; a full integrated prediction world and smoothing remain. |
| Network time and tick coordination | [Photon Fusion time synchronization](https://doc.photonengine.com/fusion/v2/manual/advanced/time-synchronization) | `[~]` Heartbeat offset/jitter and tick-rate/catch-up coordination exist; drift estimation and integrated slow-down policy remain. |
| Quantized replicated state and extrapolation | [Unity ghost snapshots](https://docs.unity.cn/Packages/com.unity.netcode%401.1/manual/ghost-snapshots.html) | `[~]` MemoryPack, field masks, quantization, compression, and extrapolation exist; generated replicated fields and lifecycle delta protocol remain. |
| Spatially scalable observers | [FishNet observers](https://fish-networking.gitbook.io/docs/guides/features/observers), [Mirror interest management](https://mirror-networking.gitbook.io/docs/manual/interest-management) | `[~]` Spatial hash and scene/team/custom visibility culling exist; the lifecycle scheduler now performs automatic observer refresh and visibility-safe lifecycle delivery, while broader scene/team integration remains. |
| Sub-tick lag compensation | [Photon Fusion lag compensation](https://doc.photonengine.com/fusion/v2/manual/advanced/lag-compensation) | `[~]` Registered bounded hitboxes, validation, retention budgets, and ray/sub-tick/sphere/box queries exist; broader gameplay integration remains. |
| Replication scheduling and parallel work | [Unreal Iris components](https://dev.epicgames.com/documentation/en-us/unreal-engine/components-of-iris-in-unreal-engine) | `[~]` Shared bounded encode cache and parallel preparation exist; dependency scheduling, automatic budgets, and dirty-component scheduling remain. |
| Explicit authority and RPC modes | [Godot high-level multiplayer](https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html) | `[~]` Authority modes and generated RPC registration exist; endpoint capability discovery and full automatic dispatch remain. |
| Native transport diagnostics and lanes | [Valve GameNetworkingSockets](https://github.com/ValveSoftware/GameNetworkingSockets) | `[x]` Native detailed status, lane configuration, lane send, queue, rate, quality, and congestion fields are exposed and build-tested. |
| P2P rendezvous and relay operation | [Valve P2P requirements](https://github.com/ValveSoftware/GameNetworkingSockets/blob/master/README_P2P.md) | `[~]` Authenticated HTTP signaling, relay plan, and traversal test harness exist; native GNS traversal and hostile-network matrix remain externally unverified. |

## Cross-check against `GS/codex(7).md`

The GS plan is a separate self-hosted PlayFab-like backend built around Nakama, Agones, PostgreSQL,
and a declarative control plane. It already plans the following adjacent capabilities:

| GS plan coverage | Relationship to this roadmap | Ownership decision |
| --- | --- | --- |
| Nakama identity, matchmaking tickets, Agones allocation, match lifecycle, join claims, and authoritative result submission | Overlaps GNS.NET's account/lobby adapters and the session/room boundary in M1. | GS owns product matchmaking/allocation and match claims. GNS.NET owns realtime admission, connection/session binding, and gameplay transport after a claim is presented. |
| Server-authoritative dedicated match simulation | Adjacent to M1-M4, but the GS document intentionally does not define moment-to-moment simulation or replication. | GS owns server process/product lifecycle. The game server using GNS.NET owns simulation and GNS.NET provides the engine-neutral replication runtime. |
| HTTP/gRPC APIs, generated OpenAPI/JSON Schema, command envelopes, and idempotency | Similar developer-experience concerns to M1/M2/M6 RPC and schema work. | Do not duplicate the GS control-plane command API inside GNS.NET. GNS.NET RPCs are connection-scoped gameplay messages and should be adaptable to the GS join-claim contract. |
| TLS, mTLS/workload identity, rate limits, join-claim validation, audit, OpenTelemetry, load/chaos tests, CI gates, backups, and runbooks | Overlaps the operational acceptance criteria in M5/M6. | GS owns platform/service operations. GNS.NET contributes transport-specific metrics, native-library CI, packet/replay tests, and game-session security evidence. |
| Server transport, NAT/relay requirements, and regional latency strategy listed as open decisions | Directly overlaps GNS.NET's P2P/signaling/TURN work in M5. | Resolve through an ADR shared by both projects; GNS.NET should expose adapters/contracts, while GS chooses deployment and service ownership. |
| PostgreSQL outbox, economy, inventory, progression, definitions, agents, and admin workflows | No material overlap with the GNS.NET replication roadmap. | Keep these in GS; GNS.NET should not grow an economy/control-plane subsystem. |

### Result of the validation

The following GNS.NET milestones are genuinely absent from the GS plan and remain valid additions:

- network object identity, spawn/despawn, ownership transfer, observer lifecycle, and gameplay RPCs;
- field-level replicated-state encoding, quantization, compression, lifecycle deltas, and automatic budgets;
- tick synchronisation, rollback/resimulation, prediction smoothing, and misprediction telemetry;
- registered hitbox history, rewind queries, sub-tick lag compensation, and visibility-safe replication;
- GNS-native lanes/stats, transport acceptance testing, signaling/TURN adapters, and native transport CI;
- engine-neutral developer tooling for gameplay replication, packet replay, and session-level load tests.

The following should be treated as shared integration requirements rather than new duplicate product
features: match claims, service identity, external telemetry conventions, regional transport policy,
and the end-to-end test that allocates a dedicated server before the gameplay client connects.

## Milestone plan

### M1 — Replication runtime foundation

- [x] Define stable `NetworkObjectId`, prefab/type ID, owner, spawn tick, and despawn reason envelopes.
- [x] Add server-owned spawn/despawn/ownership transfer with idempotent client application and reconnect rehydration; registered lifecycle journals replay automatically on resumed attach with full-graph fallback after retention.
- [x] Add registered commands, server RPCs, client RPCs, targeted RPCs, response correlation, timeout, and per-endpoint authority policies; typed MemoryPack envelopes and targeted client invocation are wired through both hosts.
- [x] Add a room/session lifecycle: `Lobby`, `Ready`, `Starting`, `InGame`, `Draining`, `Ended`; support max players, lock-after-start, late join, scene transition events, and authoritative late-join snapshots.
- [x] Add integration tests for duplicate/reordered lifecycle messages, reconnect during spawn, unauthorized RPCs, typed RPC transport envelopes, and late join.

Exit criteria: a sample game can create entities, transfer ownership, call an authenticated RPC, reconnect, and rebuild the same object graph without handwritten lifecycle envelopes.

### M2 — Production replication protocol

- [x] Add schema-generated replicated fields with field masks, dirty tracking, quantization, optional compression, and protocol/schema compatibility negotiation; generated type-specific MemoryPack encoders/decoders, dirty fingerprints, and compatibility metadata are covered.
- [x] Integrate entity create/update/remove records into automatic AOI, delta, priority, batching, and reliable/unreliable channel selection; automatic snapshot ticks schedule component changes without per-message caller plumbing.
- [x] Add per-connection byte/message budgets, queue age limits, starvation prevention, and explicit shed/drop counters; scheduler integration and metrics are covered.
- [x] Add shared snapshot encode caches and bounded parallel preparation for connections with identical baselines.
- [~] Add property-based/fuzz tests for malformed snapshots, baseline loss, schema mismatch, wraparound, and partial entity sets; deterministic malformed corpora and wraparound coverage exist, broader property-based generation remains.

Exit criteria: callers submit authoritative entities/components once; the runtime chooses fields, encodes only relevant changes, emits lifecycle deltas, and reports budget decisions without manual pipeline calls.

### M3 — Tick-synchronised prediction and time

- [~] Add heartbeat-based server clock offset, drift, jitter, and tick-rate measurement; measured heartbeat timing and drift-adjusted pacing exist, while native heartbeat sequence/loss integration remains an external transport concern.
- [x] Add client/server tick negotiation and bounded catch-up/slow-down behavior; facade and coordinated tick loop are integrated and tested.
- [x] Replace the helper-only prediction path with a rollback buffer containing input, state, and simulation metadata per tick; timestep, deterministic seed, and world version are retained and replay-visible.
- [x] Add deterministic resimulation limits, misprediction magnitude/count metrics, correction smoothing, and controlled extrapolation; covered by facade and wraparound tests.
- [x] Add tests under artificial latency, jitter, loss, duplicate snapshots, clock drift, and long rollback windows.

Exit criteria: the sample client predicts immediately, rewinds to an authoritative tick, replays inputs to present, smooths corrections, and exposes prediction cost/misprediction data.

### M4 — Visibility and lag-compensated gameplay

- [~] Add spatial-hash AOI with composable distance, scene, team, owner-only, custom visibility, and optional occlusion conditions; predicates and hidden-entity filtering exist, broader automatic lifecycle integration remains.
- [x] Add observer enter/leave events and visibility-safe spawn/despawn ordering; the lifecycle scheduler automatically refreshes AOI membership and queues reliable lifecycle delivery.
- [x] Add a registered historical hitbox/collider representation with bounded retention and memory budgets; retention, eviction, validation, and rejection metrics are tested.
- [x] Add server rewind queries for ray, sphere, and box tests with sub-tick interpolation and maximum rewind policy; authorized per-client queries are integrated and tested.
- [~] Add security tests proving hidden entities are not serialized and clients cannot select another client’s rewind time; unauthorized view-time rejection is covered, hidden-entity serialization coverage remains.

Exit criteria: a sample shooter can register hitboxes, perform an authoritative rewind query using the shooter’s measured view time, and replicate only valid observers.

### M5 — Transport, P2P, and native operations

- [x] Expose native GNS connection stats, lanes, send queues, and congestion/backpressure state through stable framework metrics.
- [~] Add a supported signaling/rendezvous adapter and TURN/relay configuration contract for ICE, with credential rotation and failure fallback; the authenticated adapter/contract exists, but native traversal remains unverified.
- [ ] Add LAN, NAT-type, IPv4/IPv6, symmetric-connect, relay, and hostile-network integration tests.
- [~] Fix and verify the current native loopback acceptance path; the Linux Docker harness now builds the native runtime and has been verified on the configured VPS, and CI requires an explicit `--insecure` loopback switch with managed auth capability evidence, but Windows/native traversal remain.
- [~] Cache vcpkg/native dependencies and publish native binaries as CI artifacts; jobs now validate x64 format and SHA-256 identity before and after transfer, but hosted CI publication has not yet executed.

Exit criteria: CI produces supported native artifacts, runs authenticated client/server transport tests, and reports throughput, RTT, loss, connection setup, and P2P traversal results.

### M6 — Developer experience and operations

- [x] Add source generators/analyzers for message IDs, replicated fields, RPC authority, schema versions, and duplicate registrations; generated IDs, field codecs, RPC metadata, and schema compatibility are integrated and tested.
- [x] Add generated API/reference docs and templates for server, client, room, entity, and auth-provider adapters.
- [x] Add structured metrics export (OpenTelemetry-compatible), server dashboards, and a player-facing network debug overlay.
- [~] Add persistent replay index/metadata, redaction, deterministic playback environments, and CI regression captures; persistent indexed replay, redaction, deterministic fingerprints, and CI verification exist, while isolated orchestration remains.
- [~] Add load-test scenarios for connections, rooms, entity counts, message sizes, packet loss, and reconnect storms with machine-readable JSON output; benchmark JSON output now exists, but native/load-dimension coverage remains.
- [~] Add compatibility policy, protocol version negotiation, migration tooling, package signing, and release smoke tests; policy and clean-consumer smoke are integrated, migration conversion and signing remain.

Exit criteria: a new developer can scaffold a server/client, define messages and replicated entities, run a deterministic network test, inspect metrics, and reproduce a captured session from CI.

## Recommended order

M1 is the highest-value gap because all later replication features need a common object/room/RPC lifecycle. M2 and M3 follow because current AOI/delta and prediction APIs are powerful primitives but still require game code to compose the full runtime. M4 adds competitive-game correctness, M5 hardens the native/P2P deployment path, and M6 makes the result adoptable and supportable by other developers.

## Reference scope

This is a capability comparison, not a claim that the referenced libraries are interchangeable or that every feature is appropriate for every game. GNS.NET deliberately remains engine-neutral; the milestones therefore target engine-neutral contracts and leave rendering, physics, prefab loading, and game simulation to adapters.
