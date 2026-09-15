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

## Replication v2 manifest validation

Replication v2 uses explicit component and field IDs. The checked-in manifest records schema versions,
wire types, optionality, and reserved IDs. Validate evolution before rollout:

```powershell
dotnet run --project tools/GnsNet.ReplicationManifest -c Release -- validate `
  --baseline .\current-replication-manifest.json `
  --candidate .\candidate-replication-manifest.json
```

The validator rejects duplicate identities, active/reserved collisions, deletion without reservation,
identity renames, schema regressions, and required field additions or wire-type changes without a schema
increment. The `verify` command compares a manifest to generated descriptors, while `emit` produces a
canonical manifest-shaped document from loaded assemblies. The validator is tooling-only and is not used
on the packet hot path.

The canonical v2 envelope remains inside the existing `NetFrame` outer framing. It uses bounded big-endian
packet metadata and length-delimited records; reliable lifecycle records and unreliable sequenced state
records are selected by the scheduler, while baselines advance only after application acknowledgements.

## Release smoke test

Run the clean-consumer check from the repository root before publishing:

```powershell
./scripts/release-smoke.ps1
```

It restores the solution, packs `GnsNet`, verifies the README and managed assembly are in the `.nupkg`,
then creates a fresh `net9.0` consumer and restores/builds it from the local package. The same check runs
for `v*` tags and manual dispatch through `.github/workflows/release-smoke.yml`.

## Migration validation and package signing

Validate a candidate release against the currently deployed manifest before rollout:

```powershell
./scripts/validate-migration.ps1 -CurrentManifest .\current-release.json -CandidateManifest .\candidate-release.json
```

The validator rejects protocol-major changes, schema changes, and backwards minor/replay-schema changes
with exit code `2`. `-AllowBreaking` is an explicit override for a planned coordinated migration and is
reported in the JSON result. A sample manifest is provided at `docs/migration-manifest.sample.json`.

Package signing uses the SDK's standard NuGet signer and is opt-in:

```powershell
./scripts/sign-package.ps1 -PackagePath .\artifacts\GnsNet.0.1.0.nupkg -CertificatePath .\release-signing.pfx
```

Without `-CertificatePath`, the command only reports whether the installed SDK supports signing and never
modifies the package. `.github/workflows/package-signing.yml` runs both a no-secret capability check and a
protected `trusted-signing` path. Configure `NUGET_SIGNING_CERTIFICATE_B64` and
`NUGET_SIGNING_CERTIFICATE_PASSWORD` as repository/environment secrets to materialize the PFX only for
the job, sign the candidate, and run `dotnet nuget verify --all`. Without those secrets, trusted signing
remains explicitly unprovisioned rather than being treated as passed.
