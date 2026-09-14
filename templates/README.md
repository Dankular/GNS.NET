# GNS.NET project templates

These copyable templates show the intended composition points for a game server:

- `contracts/Contracts.cs` — MemoryPack state and input contracts.
- `server/ServerHost.cs` — authoritative server registration and tick flow.
- `client/ClientHost.cs` — prediction, reconciliation, and interpolation.
- `auth/Admission.cs` — signed short-lived admission-token validation boundary.

They are source templates rather than a project generator; replace the transport construction and game-specific simulation code.
