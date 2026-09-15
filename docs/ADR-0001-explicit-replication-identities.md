# ADR 0001: Explicit replication identities

## Decision

New replicated components and fields use explicit numeric IDs declared by
`ReplicatedComponentAttribute` and `ReplicatedFieldAttribute`. Numeric field
order is sorted only for deterministic emission; source order, CLR metadata
order, hashes, and namespace changes never change an existing wire identity.

Schema evolution reserves retired IDs. Required unknown records reject
admission; optional unknown records may be skipped only after compatibility
negotiation. A schema version must advance for an incompatible type change.

The legacy message/schema generator remains source-compatible and keeps its
existing IDs. Replication v2 is versioned independently and carries bounded
lengths so unknown optional records can be skipped safely.
