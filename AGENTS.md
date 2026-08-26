# BGPLite - AI Agent Reference

## Core purpose

BGPLite is a lightweight BGP route server with dynamic prefix provisioning via RIPE Stat and an HTTP management API. The app runs as a .NET console application and supports BGP session management, 4-byte ASN, per-peer AS-list subscriptions, custom prefixes, SQLite peer storage, and Docker deployment.

## Architecture overview

- The server entrypoint is `BGPLite/Program.cs`.
- BGP protocol encoding/decoding (OPEN, UPDATE, KEEPALIVE, NOTIFICATION) lives in `BGPLite.Protocol/`.
- TCP listener, BGP session FSM, timers, and metrics live in `BGPLite.Server/`.
- Route table and community-based filtering live in `BGPLite.Routing/`.
- YAML config loading and config models live in `BGPLite.Configuration/`.
- HTTP management API and SQLite peer store live in `BGPLite.Api/`.
- RIPE Stat integration and prefix provisioning live in `BGPLite.Providers/`.
- Unit tests live in `BGPLite.Tests/`.

Keep the boundary clear: protocol correctness belongs in `BGPLite.Protocol`; session FSM and TCP handling belong in `BGPLite.Server`; routing and filtering belong in `BGPLite.Routing`; HTTP management and persistence belong in `BGPLite.Api`; prefix sourcing belongs in `BGPLite.Providers`; config loading belongs in `BGPLite.Configuration`.

## Tech stack

- Language: C# (.NET 10, `global.json` is the source of truth)
- Server: `Microsoft.Extensions.Hosting` + raw TCP `Socket`/`NetworkStream`
- API: raw HTTP listener (`System.Net.HttpListener`)
- Database: SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- Config: YAML (`YamlDotNet`)
- Testing: xUnit + coverlet

## Repository layout

| Path | What |
|---|---|
| `BGPLite/` | application entrypoint and DI wiring |
| `BGPLite.Protocol/` | BGP message encoding/decoding, FSM states, capabilities, path attributes |
| `BGPLite.Server/` | TCP listener, BGP session FSM, timers, metrics, service interfaces |
| `BGPLite.Routing/` | route table, route filters, community-based filtering |
| `BGPLite.Configuration/` | YAML config models and loading |
| `BGPLite.Api/` | HTTP management API endpoints, SQLite peer store, EF Core entities |
| `BGPLite.Providers/` | RIPE Stat API integration, prefix service, local prefix provider |
| `BGPLite.Tests/` | xUnit unit tests |
| `docs/` | (not yet created) |
| `.github/workflows/` | (not yet created) |

## Documentation map

Read relevant Markdown before changing behavior:

- General project behavior: `README.md`
- Contribution and commit conventions: `CONTRIBUTING.md`
- Current project notes: (none yet)

## Build and validation commands

```bash
# run all tests
dotnet test

# run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# build
dotnet build

# build in release
dotnet build -c Release

# run locally
dotnet run --project BGPLite
```

Format changed C# files with `dotnet format` before finalizing. For docs-only changes, tests are usually not required; state that they were not run because the change is documentation-only.

## Runtime entry points

- Main process: `BGPLite/Program.cs`
- BGP server: `BGPLite.Server/BgpServer.cs`
- BGP session FSM: `BGPLite.Server/BgpSession.cs`
- Management API: `BGPLite.Api/ManagementApi.cs`
- Peer store: `BGPLite.Api/PeerStore.cs`
- Route table: `BGPLite.Routing/RouteTable.cs`
- Prefix service: `BGPLite.Providers/PrefixService.cs`
- Config loader: `BGPLite.Configuration/ConfigLoader.cs`

## Agent constraints

- Prefer the smallest correct change.
- Do not run git/GitHub commands unless explicitly asked, except read-only inspection such as status/diff/log when preparing a requested commit or PR.
- Do not commit generated binaries, database files, cache contents, local config, tokens, or secrets.
- Do not add dependencies unless the user asks or there is no reasonable local implementation.
- Keep existing public API behavior stable unless the task explicitly changes it.
- Preserve user changes in a dirty working tree; never revert unrelated edits.
- Before PR creation, request/perform project review according to local policy when applicable.

## Code of conduct for agents

- Inspect nearby code before introducing new patterns.
- Keep diffs tight; avoid drive-by refactors.
- Prefer explicit state and validation over heuristics.
- Treat partial failure as normal for network operations, filesystem work, and external API calls.
- Make cancellation and retry behavior explicit.
- Do not hide data loss, verification failure, or fallback behavior.
- Finish end-to-end: implementation, focused tests, and cleanup.

## BGP protocol correctness rules

BGP protocol bugs can cause route leaks or session resets, so correctness beats cleverness.

- OPEN messages must validate ASN, hold time, and BGP identifier according to RFC 4271.
- UPDATE messages must validate prefix length, path attribute order, and well-known attribute presence.
- Keepalive messages must be sent every `KeepAlive` seconds; missed keepalives must transition the FSM to Idle.
- NOTIFICATION messages must include the correct error code/subcode per RFC 4271.
- The FSM must strictly follow the states: Idle → Connect → Active → OpenSent → OpenConfirm → Established.
- Hold timer expiry must send NOTIFICATION and reset to Idle.
- Path attributes must be encoded in the correct order: ORIGIN, AS_PATH, NEXT_HOP, MULTI_EXIT_DISC, LOCAL_PREF, COMMUNITY.

## Session and server state rules

- `BgpServer` owns live session state; callers should receive snapshots, not shared mutable pointers.
- Do not hold the session lock while sending data or writing to listeners.
- Session changes must preserve `max-active` semantics: limit concurrent sessions, reject excess connections.
- Progress events should distinguish connecting, open, established, closing, and error states.
- Graceful session teardown must send NOTIFICATION (Cease) before closing the socket.

## Database and persistence rules

- SQLite via EF Core handles peer storage; schema is managed by EF Migrations (`BgpDbContext.Initialize` applies `Database.Migrate()`; EnsureCreated-era databases are stamped and converged by the `LegacyEnsureCreated` migration).
- Do not modify the schema outside of `BgpDbContext`.
- Cache (RIPE Stat) is in-memory only; no persistent cache layer exists.
- Do not expose full connection strings through API responses or logs.
- Prefer async I/O for database and HTTP operations.

## API and UI rules

- The management API uses JSON request/response bodies.
- Server validation is authoritative; client validation is convenience only.
- Do not expose full configuration or database contents through API responses.
- API endpoints must validate peer addresses, ASN ranges, and community values.
- The management API runs on port 5000; BGP listens on port 179.

## Prefix provisioning rules

- `BGPLite.Providers` should fetch prefixes from RIPE Stat without blocking session startup.
- Cached prefixes must have a configurable TTL.
- Local prefix provider (`nets.txt`) serves as fallback when RIPE Stat is unavailable.
- AS-list subscriptions determine which ASN prefixes to provision for each peer.
- Custom prefixes override or supplement AS-list subscriptions.

## Testing expectations

- Protocol changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.BgpMessage"` at minimum.
- Server/session changes: run `dotnet test` at minimum.
- Routing changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.RouteTable"` at minimum.
- Config changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.Configuration"` at minimum.
- Cross-project behavior changes: run `dotnet test`.
- Add regression tests for protocol parsing, FSM transitions, race conditions, and config edge cases whenever practical.

## Commit and PR conventions

Follow `CONTRIBUTING.md`:

- Commit/PR title template: `<scope>: <imperative summary>`
- Common scopes: `protocol`, `server`, `routing`, `config`, `api`, `providers`, `tests`, `docs`, or `fix(<area>)`
- Keep commits focused on one logical change.
- PR body should include summary, why, validation, and risk.

## Regression-prevention checklist

- Could this change corrupt the BGP session FSM state?
- Does NOTIFICATION get sent before the socket is closed on errors?
- Does session teardown release all resources (timers, sockets, routes)?
- Does a config change affect currently running sessions or only new sessions?
- Does API state survive server restart?
- Are RIPE Stat fetch failures handled gracefully without blocking BGP sessions?
- Can a failed DB write masquerade as a successful peer registration?
- Are ASN ranges, IP addresses, and community values validated before use?

## Recent changes

- Current working notes: (none yet)
- Recent commits: `git log --oneline -10`
