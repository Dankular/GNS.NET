# ADR 0002: Monotonic network clock discipline

## Decision

Runtime clock discipline uses `INetworkTimeSource` timestamps and an NTP-style
four-timestamp sample. `DateTimeOffset.UtcNow` is not part of synchronization.
The clock exposes authoritative, prediction, and render timelines, adapts
interpolation delay from jitter, slews ordinary corrections, and bounds
catch-up. Initial acquisition, reconnect, and detected epoch discontinuity may
hard-resynchronize.

Tests use deterministic manual timestamp sources. Wall-clock values remain
available only to application telemetry and persistence layers.
