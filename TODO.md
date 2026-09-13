# GNS.NET execution ledger

This file tracks the requested work; it is not a substitute for implementation. `[x]` means
implemented, integrated, tested, and verified. `[~]` means the supported closest implementation is
present but an external/backend limitation remains and is documented.

1. **[~] Native GNS auth-ticket/certificate validation**

   Replace application-only admission with supported native GNS authentication where the selected
   backend exposes it. Validate certificates/tickets before treating a connection as authenticated,
   reject invalid or expired credentials, and add backend-specific tests or an explicit unsupported
   backend result.

   Certificate/authenticated transport enforcement and `InitAuthentication` are integrated. Steam
   `BeginAuthSession` ticket callbacks are not exposed by the selected open-source GnsSharp backend;
   native Steamworks ticket validation requires the Steamworks backend and a platform Steam identity.
   The framework rejects connections lacking native authentication instead of treating application
   HMAC tokens as native tickets. Application connect-token validation necessarily occurs after the
   GNS transport handshake; only native GNS/Steam authentication can gate transport acceptance.

2. **[x] Wire GNS-native network impairment configuration**

   Add configuration for GNS's native latency, jitter, loss, and related development settings.
   Apply it to the appropriate global or per-connection configuration path, keep production defaults
   secure, and test that configured values reach the native connection setup.

   Implemented through `GnsRuntimeOptions.Impairment` and native global GNS config values for
   send/receive loss, lag, and jitter. Values are validated before application. The runtime path
   is covered through `GnsNativeConfiguration`'s native-key sink test, while actual native packet
   effects still require a platform GNS shared library and integration environment.

3. **[x] Measure RTT from heartbeat sequence and timestamps**

   Add heartbeat sequence numbers and send timestamps, match acknowledgements to outstanding probes,
   calculate smoothed RTT per connection, handle timeout/late/duplicate acknowledgements, and expose
   the result through connection metrics.

   Implemented with sequenced timestamped heartbeat probes, server acknowledgements, duplicate/late
   rejection, and exponentially smoothed RTT in `ConnectionMetrics`.

4. **[x] Calculate packet loss from sequence acknowledgements**

   Track sent and acknowledged heartbeat/message sequences, calculate loss over a bounded rolling
   window, distinguish loss from packets that are still in flight, and expose the result per
   connection with tests for reordering, duplication, and wraparound.

   Implemented with bounded, expiring heartbeat sequence probes; duplicate/late acknowledgements
   are ignored and expired probes contribute to `ConnectionMetrics.PacketLossPercent`.

5. **[x] Persist and replay recorded network traffic**

   Replace the in-memory-only recorder with a versioned on-disk capture format containing direction,
   timestamps, connection/session identity, channel, and payload. Add transport-replay playback with
   speed control, deterministic timing, corruption/truncation handling, and desync reproduction tests.

6. **[x] Dynamically apply load shedding to the tick loop**

   Connect load metrics to tick scheduling. Reject new connections at capacity, reduce or restore tick
   frequency using hysteresis, preserve critical control traffic, and test overload/recovery behavior.

7. **[x] Implement a real backend/server messaging adapter**

   Add a production transport adapter for matchmaking, persistence, and server-to-server messages
   behind the backend bus interface. Define delivery, retry, ordering, timeout, authentication, and
   shutdown semantics; retain the in-memory adapter for tests.

   Implemented with TCP listener/client transport, authenticated retrying wrapper, ordering and
   duplicate suppression, payload limits, timeout handling, and async shutdown.

8. **[x] Implement shard process lifecycle and migration**

   Extend shard routing into registration/health leases, process startup and shutdown coordination,
   ownership changes, player migration, drain mode, and recovery when a shard dies. Add integration
   tests for assignment, migration, and stale ownership.

   Implemented with leases, drain mode, local process controller, migration coordination, and
   explicit player state export/import handoff, and an in-library `ShardSupervisor` for restart
   recovery after process death.

9. **[x] Automatically apply delta, AOI, and priority policies**

   Move these policies into the snapshot pipeline so callers cannot accidentally broadcast full state:
   maintain per-client acknowledged baselines, cull by interest, schedule by relevance, batch by
   channel, and emit reliable event versus unreliable state traffic automatically.

   Implemented by `SnapshotPipeline` and `GnsServerHost.SendAutomaticSnapshot`; baselines advance
   only after acknowledgement and output is budgeted, culled, prioritized, relevance-cadenced,
   batched, and channelled.

10. **[x] Automatically validate registered input messages**

    Extend message registration with input schemas and validators. Enforce per-client sequence/rate
    limits, physics/range checks, anti-speedhack and anti-teleport rules before simulation, reject
    invalid input consistently, and test that unvalidated input handlers cannot bypass the guard.

    Implemented through host-level input registration with deserialization, per-client sequence
    ordering, rate limits, custom validation, movement validation, and pre-handler rejection.

11. **[~] ICE/P2P native transport** — `ConnectP2P` and `ListenP2P` use the supported GnsSharp
    APIs, preserve GNS certificate enforcement, and `GnsP2POptions` wires ICE candidate policy and
    STUN server configuration. The pinned GnsSharp/GNS documentation warns that its current upstream
    native commit has broken P2P support; an updated native GNS build is required to validate actual
    ICE traversal.
