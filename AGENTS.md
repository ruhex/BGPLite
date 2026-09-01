# BGPLite - AI Agent Reference

## Core purpose

BGPLite is a lightweight BGP route server with dynamic prefix provisioning via RIPE Stat and an HTTP management API. The app runs as a .NET console application and supports BGP session management, 4-byte ASN, per-peer AS-list subscriptions, custom prefixes, SQLite peer storage, and Docker deployment.

## How to use this document

- Treat this document as project guidance, not as a substitute for inspecting the current source code. If this document conflicts with the current implementation, verify the discrepancy before making changes.
- Prefer current source code, tests, and executable architecture checks (`LayeringTests`, `CompositionContractTests`) over stale documentation.

## Architecture overview

A modular monolith with strict one-directional layering: `BGPLite.Protocol` (codecs, BCL-only leaf) and `BGPLite.Contracts` (shared interfaces/DTOs, dependency-free leaf) sit at the bottom; `BGPLite.Routing` and `BGPLite.Providers` build on them; `BGPLite.Server` owns the session FSM and TCP; `BGPLite.Api` owns the management API and persistence; the `BGPLite` app is the composition root.

The dependency matrix, layer responsibilities, composition-root wiring, seams, and data flow are detailed in `docs/ARCHITECTURE.md`. The layering rules are enforced by `LayeringTests`/`CompositionContractTests` — violations fail CI, not review.

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
| `BGPLite.Contracts/` | shared contracts: IPeerStore, ISessionManager, IPrefixService, peer DTOs, BgpMetrics (dependency-free leaf) |
| `BGPLite.Protocol/` | BGP message encoding/decoding, FSM states, capabilities, path attributes (pure leaf: BCL-only, no BGPLite.* refs, no packages — enforced by `LayeringTests`) |
| `BGPLite.Server/` | TCP listener, BGP session FSM, timers, route assembly |
| `BGPLite.Routing/` | route table, route filters, community-based filtering, prefix aggregation |
| `BGPLite.Configuration/` | YAML config models and loading |
| `BGPLite.Api/` | HTTP management API endpoints, SQLite peer store, EF Core entities |
| `BGPLite.Providers/` | RIPE Stat API integration, prefix service, file/HTTP/URL prefix providers |
| `BGPLite.Tests/` | xUnit unit tests (incl. executable architecture rules: `LayeringTests`, `CompositionContractTests`) |
| `docs/` | `ARCHITECTURE.md` (detailed architecture), `DESIGN_DECISIONS.md` (decision/RFC-deviation catalog) |
| `.github/workflows/` | CI: `ci.yml`, `codeql.yml`, `format.yml`, `release.yml`, `release-assets.yml` |

## Documentation map

Read relevant Markdown before changing behavior:

- General project behavior: `README.md`
- Contribution and commit conventions: `CONTRIBUTING.md`
- Detailed architecture and layer responsibilities: `docs/ARCHITECTURE.md`
- Deliberate decisions and RFC deviations: `docs/DESIGN_DECISIONS.md` (referenced from rules below as D1–D15)
- Protocol conformance status: `docs/RFC_COMPLIANCE.md` (generated from a 2026-07-02 audit — may lag behind `main`; verify file:line before acting)
- Release history: `CHANGELOG.md`

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

After making a code change:
1. build the affected project;
2. run the most relevant tests;
3. fix failures caused by the change;
4. run the broader test suite when required;
5. format changed C# files.

Format only the files affected by the change when practical (`dotnet format --include <changed files>`); avoid introducing unrelated formatting changes. For docs-only changes, tests are usually not required; state that they were not run because the change is documentation-only.

## Runtime entry points

Where execution starts — for everything else, start from the Repository layout above.

- Main process and DI composition root: `BGPLite/Program.cs`
- BGP listener: `BGPLite.Server/BgpServer.cs` → per-connection FSM: `BGPLite.Server/BgpSession.cs`
- Management API: `BGPLite.Api/ManagementApi.cs` (persistence behind it: `BGPLite.Api/PeerStore.cs`)

## Agent constraints

- Prefer the smallest correct change.
- Do not run git/GitHub commands unless explicitly asked, except read-only inspection such as status/diff/log when preparing a requested commit or PR.
- Before creating a GitHub issue, search existing issues and PRs for duplicates or closely related reports.
- Do not commit generated binaries, database files, cache contents, local config, tokens, or secrets.
- Do not manually edit generated files (e.g. EF Core migrations, their `.Designer.cs`, and the model snapshot). Modify their source/configuration and regenerate them when required.
- Do not add dependencies unless the user asks or there is no reasonable local implementation.
- Keep existing public API behavior stable unless the task explicitly changes it.
- Preserve user changes in a dirty working tree; never revert unrelated edits.
- Before PR creation, request/perform project review according to local policy when applicable.

## Code of conduct for agents

- Before modifying protocol, FSM, routing, persistence, or API behavior, inspect the existing implementation, relevant tests, interfaces, and call sites. Do not make changes based on a single file in isolation.
- Inspect nearby code before introducing new patterns.
- Keep diffs tight; avoid drive-by refactors.
- Prefer explicit state and validation over heuristics.
- Treat partial failure as normal for network operations, filesystem work, and external API calls.
- Make cancellation and retry behavior explicit.
- Do not hide data loss, verification failure, or fallback behavior.
- Finish end-to-end: follow the post-change loop in "Build and validation commands" — build → focused tests → fix → broader suite → format.

## BGP protocol correctness rules

BGP protocol bugs can cause route leaks or session resets, so correctness beats cleverness.

RFC policy:

- When changing BGP protocol behavior, consult the applicable RFC before implementing or modifying behavior. Do not infer protocol semantics from existing code alone.
- RFC requirements take precedence over assumptions, existing implementation patterns, or comments when determining protocol behavior.
- Applicable RFCs beyond the base spec (RFC 4271): RFC 5492 (capabilities advertisement), RFC 6793 (4-octet AS number), RFC 7606 (revised UPDATE error handling), RFC 7607 (AS 0 rejection), RFC 4486 (Cease subcodes), RFC 1997 (communities), RFC 8092 (large communities), RFC 2918 (route refresh), RFC 4724 (graceful restart).

Rules:

- OPEN messages must validate ASN, hold time, and BGP identifier according to RFC 4271 (§4.2/§6.2); hold time 1–2 s MUST be rejected with Unacceptable Hold Time.
- UPDATE messages must validate NLRI/prefix encoding, attribute flags and lengths, required well-known attributes, and other constraints required by the applicable RFCs. Do not assume a fixed path-attribute ordering unless required by the relevant specification.
- KEEPALIVE interval is derived from the negotiated hold time: `max(negotiatedHoldTime / 3, 1)` (`OpenNegotiator`); the configured `KeepAlive` value is validated against HoldTime/3 but the wire interval always follows the negotiated hold time.
- NOTIFICATION messages must include the correct error code/subcode per RFC 4271; exactly one NOTIFICATION per teardown (enforced by the `_teardownReason` CAS latch in `BgpSession`).
- The implementation uses a passive-only FSM subset. For inbound sessions, the normal path is Idle → Connect → OpenSent → OpenConfirm → Established. Active and ConnectRetryTimer are intentionally not implemented (D1, #10 item 1). Do not reintroduce them accidentally.
- Hold timer expiry must send NOTIFICATION (Hold Timer Expired) and reset to Idle; hold time 0 disables both timers (RFC 4271 §4.2/§6.5).
- The writer currently emits path attributes in ascending type-code order (ORIGIN, AS_PATH, NEXT_HOP, COMMUNITY, AS4_PATH, LARGE_COMMUNITY) — an implementation choice, not an RFC requirement (D15).

Deliberate deviations from the RFCs are catalogued in `docs/DESIGN_DECISIONS.md` (D1–D15, with context and trackers). Check that catalog before "fixing" behavior that looks wrong — it is usually a recorded decision, not a bug; when you close or change one, update the catalog.

## Session and server state rules

- `BgpServer` owns live session state; callers should receive snapshots, not shared mutable pointers.
- Do not hold the session lock while sending data or writing to listeners.
- Avoid blocking synchronous I/O on BGP session threads (the send/read paths are async end-to-end, including the persistence contract: `IPeerStore` is async-only since #262 — keep new store members async).
- CancellationToken must propagate through long-running network and background operations; OCE from caller cancellation is never swallowed or cached as a failure (#114/#225/#254).
- Timer callbacks and session shutdown must be safe when executed concurrently — cross-thread teardown coordination goes through atomic latches (see the `_teardownReason` CAS in `BgpSession`), not read-then-write flags.
- Session disposal must be idempotent — use the `Interlocked.Exchange` test-and-set pattern (`BgpSession.Dispose`, `SocketBgpConnection.Dispose`); a second `Dispose` must be a no-op, not a double-release.
- Session capacity: there is deliberately NO global max-active session cap (D9) — a route server is designed to hold many peers; inbound-connect floods are bounded per source IP by `Bgp.MaxAcceptsPerIpPerMinute` instead. Reintroducing a global cap is a product decision, not a bug fix.
- Progress events should distinguish connecting, open, established, closing, and error states.
- Normal local teardown sends exactly one NOTIFICATION (Cease) before closing the socket. Deliberate silent-close paths (Graceful Restart-aware shutdown, session replacement, and peer NOTIFICATION handling) must not send an additional NOTIFICATION.

## Error handling

- Do not catch exceptions only to suppress them. Preserve enough context for diagnosis — every best-effort `catch` in this codebase either logs (with the exception) or documents why silence is correct for that one path.
- Distinguish protocol errors, peer/network errors, configuration errors, and internal application errors — the codebase's existing vocabulary:
  - protocol: `BgpParseException` / `BgpNotificationException` carrying RFC 4271 error code/subcode (routed to NOTIFICATION, treat-as-withdraw, or session reset per RFC 7606);
  - peer/network: `IOException` / `SocketException` — normal partial failure, logged at Warning, not a server fault;
  - configuration: fail loud at startup (`config.Validate()`, #89) — never a runtime catch-and-continue;
  - internal: the generic catch → Cease + `LogError` with the exception is the last resort, not the default.
- Do not collapse these categories into `catch (Exception) { }` — it turns a diagnosable protocol/network event into an undefined teardown.

## Database and persistence rules

- SQLite via EF Core handles peer storage. Database schema changes must be implemented through EF Core migrations and the existing database initialization/migration mechanism.
- Do not modify the schema outside of `BgpDbContext`.
- Cache (RIPE Stat) is in-memory only; no persistent cache layer exists.
- Do not expose full connection strings through API responses or logs.
- Prefer async I/O for database and HTTP operations.

## API and UI rules

- The management API uses JSON request/response bodies.
- Server validation is authoritative; client validation is convenience only.
- Do not expose full configuration or database contents through API responses.
- API endpoints must validate peer addresses, ASN ranges, and community values.
- The management API listens on `ApiPort` (default 5001, loopback by default); BGP listens on `BgpConstants.BgpPort` (179). Do not hardcode either port — reference the constant/config.

## Security

BGPLite is a network-facing BGP route server with an HTTP management plane — treat both as exposed.

- Never log credentials, tokens, secrets, or sensitive peer configuration. Peer-supplied source URLs may carry query-string tokens — log the source name, never the URL (#149); use `SanitizeForLog` for operator-supplied strings.
- Never accept unvalidated peer-controlled values into management APIs. Server validation is authoritative (#255).
- Treat BGP UPDATE data (NLRI, path attributes, communities) as untrusted input.
- Validate message lengths before allocation — parse against the declared section bounds (withdrawn/attribute length, prefix bytes), never read past them.
- Avoid unbounded memory allocation based on peer-provided values: HTTP response bodies are capped (`MaxResponseBytes`, #144), inbound caches are bounded (#165, #261) — keep new paths bounded too.
- Do not disable validation merely to make a malformed peer message work; route it through the documented error handling (RFC 7606 / treat-as-withdraw) instead.
- The management API binds to loopback by default and is intentionally unauthenticated; do not widen the bind address or trust boundary without an explicit product/security decision.

## Prefix provisioning rules

- `BGPLite.Providers` should fetch prefixes from RIPE Stat without blocking session startup.
- Cached prefixes must have a configurable TTL.
- Local prefix provider (`nets.txt`) serves as fallback when RIPE Stat is unavailable.
- AS-list subscriptions determine which ASN prefixes to provision for each peer.
- Custom prefixes override or supplement AS-list subscriptions.

## Testing expectations

- Before changing behavior, locate existing tests covering the affected code and extend them when the behavior changes.
- Protocol changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.BgpMessage"` at minimum.
- Server/session changes: run `dotnet test` at minimum.
- Routing changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.RouteTable"` at minimum.
- Config changes: run `dotnet test --filter "FullyQualifiedName~BGPLite.Tests.Configuration"` at minimum.
- Cross-project behavior changes: run `dotnet test`.
- The listed test commands are minimum validation requirements, not necessarily the only validation required. Use broader validation when the change crosses project boundaries or affects shared behavior.
- Add regression tests for protocol parsing, FSM transitions, race conditions, and config edge cases whenever practical.
- When fixing a bug, add or update a regression test that fails before the fix and passes after it, unless the behavior cannot reasonably be tested automatically.

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
