# Compatibility and migration policy

GNS.NET separates transport, protocol, and application schema compatibility:

1. Native transport compatibility is pinned by the `GnsNetBackend` and the native GNS artifact. Upgrade native artifacts as a coordinated server/client release.
2. `ProtocolVersion.Major` changes are breaking. A client and server must reject different major versions.
3. `ProtocolVersion.Minor` is negotiated to the lower common value; new optional features must remain backward compatible at the same major.
4. Frame and generated replication schema versions must match before payload decoding. A schema mismatch is rejected, never guessed.
5. Replay files are versioned and must be migrated or rejected explicitly; do not silently reinterpret old captures.

Recommended migration sequence:

- Deploy servers that understand both old and new application messages.
- Deploy clients with the negotiated new minor version.
- Raise the major/schema version only after the old client cohort is retired.
- Keep a rollback server artifact and native library matching the previous protocol.

Every breaking release should add a protocol negotiation test and a release smoke test for the migration path.
