# Shard process protocol

`LocalShardProcessController` starts each shard with standard input and output redirected. The
controller writes one JSON command per line. A process must emit an acknowledgement line on
standard output after it has durably accepted the command:

```json
{"type":"player.migrate.ack","operationId":"<guid>","shard":"<source-or-target>","accepted":true}
```

Migration is committed only after acknowledgements from both source and target are received. The
state transfer adapter runs between command dispatch and lifecycle commit; if either process does
not acknowledge within ten seconds, the player remains assigned to the source shard and the state
transfer is not imported. Deployed game servers should treat the command as idempotent by
`operationId` and emit `accepted:false` for invalid or duplicate ownership transitions.
