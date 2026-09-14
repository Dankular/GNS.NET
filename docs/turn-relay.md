# TURN relay harness

`docker/turn-compose.yml` runs coturn on the configured VPS using TURN REST API
shared-secret authentication. It is a relay service, not a GNS server and does not
contain game or PlayFab credentials.

On the VPS, set `TURN_SHARED_SECRET`, `TURN_REALM`, and the VPS public `TURN_EXTERNAL_IP`
in the deployment environment, then run:

```sh
docker compose --env-file turn.env -f docker/turn-compose.yml up -d
```

Open UDP/TCP 3478 and UDP 49152–49252 in the VPS firewall/security group. The relay
pool should be published by the GS/matchmaker signaling endpoint as short-lived
`P2PRelayEndpoint` values. `TurnCredentialRotator` derives coturn-compatible
`expiry:user` usernames and HMAC-SHA1 credentials; clients receive only those derived
values. Issue new credentials before expiry and use `SelectFallback` to skip expired or
incomplete relays.

## GNS.NET boundary

The managed API configures native ICE, STUN discovery, and the supported GNS TURN
server/user/password lists through `GnsP2POptions.TurnRelays`. Credentials are
validated as short-lived `turn:`/`turns:` values and are never written to logs by
the framework. A public two-peer traversal run is still required to prove that the
selected native build and deployed coturn instance establish a relayed path, with
both peers outside the VPS network.

## GS integration

GS (Nakama/Agones/control-plane) should authorize the match, call its signaling service,
and return the relay plan. It must not return `TURN_SHARED_SECRET`. GNS.NET only needs
the resulting bearer-authenticated `P2PTraversalPlan`; PlayFab or another backend can
implement the same publish/poll HTTP contract.

## Verified VPS deployment

The GS deployment owns the shared Coturn instance on the build VPS; GNS.NET must not start a
second Coturn container on the same host or duplicate the shared secret. The deployed instance was
validated on 2026-09-14 with Coturn 4.6.3 and the published UDP/TCP 3478 plus UDP relay range:

1. The VPS Docker/Compose deployment was inspected and its Coturn container was running.
2. An authenticated `turnutils_uclient` allocation probe ran with the deployment's secret kept
   inside the host and received relay allocations in the configured public relay range.
3. The probe exchanged 20/20 test packets with 0% loss and completed channel binds.
4. The GNS.NET native container was built on the same VPS and its real native loopback harness
   completed 2,000 echoed messages with 0% measured loss.

This is deployment and single-host allocation evidence, not the missing two-external-peer proof.
The VPS check does not close the NAT, symmetric-NAT, hostile-network, or public relayed-path matrix;
those still require independently networked peers. The existing deployment is also configured
without TURN TLS, so `turns:` endpoints require a separately provisioned certificate and listener.
