# GameService integration

GNS.NET owns the realtime game session after the platform has matched and allocated a
server. The GameService control plane remains responsible for identity, matchmaking,
allocation, and issuing match-scoped join claims.

## Connection flow

1. The game authenticates the player with Nakama and receives its session JWT.
2. The game creates a matchmaking ticket at `POST /v1/matchmaking/tickets`.
3. The game polls the ticket and match until the match has an allocated server address.
4. The game requests `POST /v1/matches/{matchId}/join-claims` with the Nakama session.
5. The game sends the returned short-lived claim to the allocated game server's join endpoint.
6. After the game server verifies the claim against the GameService Ed25519 public key,
   it issues or accepts a GNS.NET admission token for the realtime transport.
7. The client connects to GNS.NET using the allocated address and the admission token.

The GameService join claim is not a raw UDP credential. It is an HTTP/control-plane
authorization artifact containing match, allocation, build, roster, slot, and expiry
constraints. The GNS.NET admission token is connection-scoped and must be short-lived.
Never place the GameService signing private key, Nakama signing secret, or transport
admission secret in a client, Docker image, or repository.

## Game-server boundary

The game server should verify the claim before attaching a `GnsServerHost` session:

```csharp
// `subject`, `matchId`, `allocationId`, and `build` come from the verified GS claim.
var admission = new ConnectionAdmission(
    new ConnectTokenService(Convert.FromBase64String(
        Environment.GetEnvironmentVariable("GNS_ADMISSION_SECRET")!)));

var host = new GnsServerHost<string>(server, TimeSpan.FromSeconds(30), admission);
// Issue a short-lived GNS token only after GS claim verification and roster checking.
string transportToken = tokenService.Issue(subject, TimeSpan.FromSeconds(30));
```

The exact GS claim verification remains in the GameService process/library because its
Ed25519 key and match database are platform concerns. A game-specific adapter should
call the GS `/join` endpoint, then map the verified subject to the GNS session ID.

## Local development

For a local POC without platform credentials, both sides may use the explicit
development-only `--insecure` switch. This bypasses application admission and must not
be used for a public server. Production-like local testing should instead run a small
claim issuer/verifier fixture with generated keys and a separate GNS admission secret.

## Native/P2P deployment

The allocated `serverAddress` and `serverPorts` are the rendezvous inputs for the
transport adapter. Direct UDP, relay/TURN, and fallback candidates should be selected
by the native transport layer after the claim has been validated; a successful HTTP
join claim does not prove that NAT traversal or native certificate authentication is
working. See [`native-runtime-container.md`](native-runtime-container.md) for the
Docker/VPS validation boundary.
