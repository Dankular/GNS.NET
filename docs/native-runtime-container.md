# Native runtime container

The repository includes a reproducible Linux native-runtime harness. It compiles the selected
GameNetworkingSockets revision, builds GNS.NET against the managed package, runs the complete
managed test suite, and then launches the native loopback benchmark.

From a machine with Docker:

```powershell
docker compose -f docker/docker-compose.native.yml build --pull
docker compose -f docker/docker-compose.native.yml run --rm gnsnet-native-ci
```

Pin the native source for repeatable CI or VPS runs with `GNS_REF`:

```powershell
$env:GNS_REF = "<known-good-commit>"
docker compose -f docker/docker-compose.native.yml build
```

`GNS_REF` accepts a branch, tag, or raw GameNetworkingSockets commit SHA. The Dockerfile fetches
and checks out the requested ref explicitly, so VPS runs can use the same reviewed commit as hosted
CI instead of silently building the default branch.

The build VPS uses the same compose file and reviewed default. To run the harness there through the
existing VPS helper, refresh `/opt/gnsnet-native-cfa96ce` to the target branch and run:

```powershell
cmd.exe /c vps.cmd exec "docker compose -f /opt/gnsnet-native-cfa96ce/docker/docker-compose.native.yml build --pull"
cmd.exe /c vps.cmd exec "docker compose -f /opt/gnsnet-native-cfa96ce/docker/docker-compose.native.yml run --rm gnsnet-native-ci"
```

The VPS validation is useful for native Linux loading and controlled impairment, but it is still a
single-host loopback test. It does not substitute for two external peers behind independent NATs.

The container is intentionally a verification harness, not a production game-server image. Do not
put PlayFab, Steamworks, or TURN secrets in the image or compose file. Native traversal still needs
separate public-network peers and relay credentials; a local container can verify loading, handshake,
and loopback behavior but cannot prove NAT traversal.

## CI native artifact contract

The `native-linux` and `native-windows` workflow jobs publish the native library together with
`gns-artifact.sha256`. Each producer validates that the output is non-empty and x64 before upload:
Linux must be an ELF x86-64 shared object, while Windows must be an AMD64 PE image. The consumer
job downloads both artifacts and runs the recorded SHA-256 checks, so a successful workflow proves
that the artifact received by the downstream job is the exact artifact produced by its platform job.

Manual runs can pin the GameNetworkingSockets source with the `gns_ref` workflow input. Ordinary
push, pull-request, and scheduled runs use the reviewed commit pinned in `.github/workflows/dotnet.yml`;
production consumers should still review and promote native revisions deliberately.
This validates packaging and runtime artifact identity, but it does not claim Windows execution on
Linux or native NAT/TURN traversal. Those require platform runners and public-network peers.

The native loopback benchmark also requires `--insecure` explicitly. CI verifies that omitting this
switch fails, then runs the managed connection-admission and external-authentication contract tests
and publishes `native-auth-capability.json`. This is the safest in-repository authentication coverage:
it proves that insecure mode cannot be accidental and that application admission/auth contracts pass
without embedding credentials. When the protected `GNS_NATIVE_CERTIFICATE_B64` secret is configured,
the `native-authenticated-linux` job additionally materializes it only for the job and runs the real
authenticated/encrypted native client/server probe. Without that secret, native certificate coverage
remains unprovisioned. Neither path validates Steamworks tickets or public-network authentication.
