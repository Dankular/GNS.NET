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

The container is intentionally a verification harness, not a production game-server image. Do not
put PlayFab, Steamworks, or TURN secrets in the image or compose file. Native traversal still needs
separate public-network peers and relay credentials; a local container can verify loading, handshake,
and loopback behavior but cannot prove NAT traversal.
