# BGPLite Architecture

Detailed architecture reference. `AGENTS.md` carries the short version; this document holds the
details. Verified against `main @ 64ce3a6` (2026-08-27 audit).

## Shape

A modular monolith: one deployable console app, eight projects, strict one-directional layering.
No cycles. The boundaries are enforced by tests, not by convention alone (see "Enforced rules").

## Dependency graph

```
                    ┌─────────────────┐
                    │   BGPLite (app) │  composition root: Program.cs, hosted services
                    └────────┬────────┘
                             │ references everything below
     ┌───────────┬───────────┼────────────┬─────────────┐
     ▼           ▼           ▼            ▼             ▼
┌─────────┐ ┌─────────┐ ┌─────────┐ ┌──────────┐ ┌────────────┐
│ Server  │ │   Api   │ │Providers│ │ Routing  │ │Configuration│
└────┬────┘ └────┬────┘ └────┬────┘ └────┬─────┘ └──────┬──────┘
     │           │           │           │        (YamlDotNet; → Protocol
     ▼           │           ▼           ▼         for CommunityCodec, #327)
┌─────────┐     │      ┌──────────────────────┐
│Protocol │     │      │ Contracts (pure leaf)│
│(BCL-only│     │      │ no dependencies      │
│  leaf)  │     │      └──────────────────────┘
└─────────┘     │
                └── Api also → Providers, Routing, Contracts, Configuration
```

Direct references (from the `.csproj` files):

| Project | References | External packages |
|---|---|---|
| `BGPLite.Contracts` | — (dependency-free leaf) | — |
| `BGPLite.Protocol` | — (BCL-only leaf) | — |
| `BGPLite.Configuration` | Protocol (CommunityCodec for config validation, #327) | YamlDotNet |
| `BGPLite.Routing` | Configuration, Protocol | — |
| `BGPLite.Providers` | Contracts, Configuration, Protocol | M.E.Hosting.Abstractions, M.E.Http(+Resilience), Logging.Abstractions |
| `BGPLite.Server` | Contracts, Configuration, Protocol, Routing | M.E.Hosting.Abstractions, Logging.Abstractions |
| `BGPLite.Api` | Contracts, Configuration, Providers, Routing | EF Core Sqlite, System.Threading.RateLimiting |
| `BGPLite` (app) | all of the above | M.E.Hosting, EF Design (build-time) |

Notable forbidden edges (subset): `Api → Server` (removed by #230 — the HTTP layer must not see
FSM/transport types), `Providers → Server` (#88), anything → `Contracts`/`Protocol` (leaves stay
leaves).

## Enforced rules — architecture as executable tests

- `LayeringTests.Dependency_graph_has_no_forbidden_edges` — a 23-edge forbidden matrix asserted via
  `Assembly.GetReferencedAssemblies()`. Any PR that re-adds an illegal edge turns CI red.
- `LayeringTests.Protocol_assembly_is_a_pure_leaf` — `BGPLite.Protocol` must reference nothing but
  BCL assemblies (it is being extracted as a standalone library, #271).
- `CompositionContractTests.ProductionDependency_IsRequired` — the collaborators that used to be
  nullable-optional (#263) must stay non-nullable with no constructor defaults, so an incomplete
  DI composition fails at build/container validation, not as silent wrong behavior.

## Layer responsibilities

| Layer | Owns | Must not |
|---|---|---|
| `BGPLite.Protocol` | message codecs (OPEN/UPDATE/KEEPALIVE/NOTIFICATION/ROUTE_REFRESH), attribute/codec validation, OPEN negotiation, FSM state enum | reference any BGPLite.* project or package |
| `BGPLite.Contracts` | `IPeerStore`, `ISessionManager`, `IPrefixService`, peer DTOs (`PeerInfo`, `PeerRoutingView`, `CustomSourceView`), `BgpMetrics` | expose EF entities, transport types, or persistence semantics |
| `BGPLite.Configuration` | YAML models + `ConfigLoader`, fail-loud `Validate()` | know about the server runtime |
| `BGPLite.Routing` | `RouteTable` (owner-tagged entries), `IRouteFilter`, `ExactUnionPrefixAggregator` | do I/O |
| `BGPLite.Server` | TCP accept loop, `BgpSession` FSM/timers, route assembly (`RouteAssembler`, `SharedTableRouteAssembler`), transport seam (`IBgpConnection`) | touch EF/HTTP directly |
| `BGPLite.Providers` | RIPEstat client, file/HTTP/ASN prefix sources, `PrefixService` caches | reference Server |
| `BGPLite.Api` | `ManagementApi` (raw `HttpListener`), `PeerStore` + EF Core entities/migrations | reference Server |
| `BGPLite` (app) | `Program.cs` composition root, hosted services (seeding, auto-refresh, config reload) | contain business logic |

## Composition root

`Program.cs` builds the whole graph explicitly:

- `ValidateOnBuild = true` — a missing registration is a startup failure naming the service (#263).
- All application services are singletons; EF access goes through `IDbContextFactory<BgpDbContext>`
  (a scoped `AddDbContext` registration exists only for the startup init scope). No captive
  dependencies (audited 2026-08-27).
- `BgpServer` is registered as a singleton first, then adopted by `AddHostedService`; `ISessionManager`
  resolves to that same instance (#231).
- Hosted services, in start order: `RouteSeedingService` (background seeding, #251) → `BgpServer`
  → `ManagementApi` → `ConfigReloader` (hot-reload of soft config) → `PrefixAutoRefreshService`.

## Key seams (deliberate abstractions)

- `IBgpConnection` — transport seam over `Socket`/`NetworkStream` (`SocketBgpConnection`), with a
  per-send budget and a send-fault latch (#252/#285). Tests drive sessions through `ScriptedConnection`.
- `IBgpSessionFactory` — the accept loop carries none of the session's dependencies (#263).
- `IRouteAssembler` — outbound policy. `RouteAssembler` (per-peer decision tree) vs
  `SharedTableRouteAssembler` (explicit, logged-once degraded mode serving only the startup seed, #307).
- `TimeProvider` — injected clock for hold timers, debounces, TTLs (#96); tests inject fakes.
- `IPrefixAggregator` — wire summarization policy (`ExactUnionPrefixAggregator`, swappable).

## Data flow

Inbound: `BgpServer` accepts → `BgpSessionFactory` → `BgpSession.RunAsync` FSM → `HandleUpdateAsync`
validates via `UpdateCodec.ParseRouteAttributes` (RFC 7606 pipeline) → accepted NLRI lands in the
shared `RouteTable` owned by that session (`owner: this`, #289). On any transition out of
Established the session's entries are flushed (`RemoveAllOwnedBy`, #313/#314).

Outbound: `SendAllRoutesAsync` → `RouteAssembler.BuildOutboundRoutesAsync` (per-peer subscriptions /
custom prefixes / custom ASNs / user URL sources, filtered by `IRouteFilter`) → `ExactUnionPrefixAggregator`
→ community-set batching (≤100 NLRI per UPDATE) → writer. Refresh path reuses this via
`RefreshRoutesAsync` with a coalescing debounce (#254).

## Persistence

SQLite via EF Core, owned entirely by `BGPLite.Api`: `BgpDbContext`, migrations (incl.
`LegacyEnsureCreated` convergence for EnsureCreated-era databases), WAL/busy_timeout pragmas via
interceptor. Schema changes go through EF migrations only (see AGENTS.md). Known gap: `PeerStore`
is fully synchronous on async paths — tracked in #262.

## Related documents

- `AGENTS.md` — agent rules (short version of everything here).
- `docs/DESIGN_DECISIONS.md` — the deliberate decisions and RFC deviations behind this shape.
- `RFC_COMPLIANCE.md` — section-by-section RFC conformance snapshot (2026-07-02; may lag `main`).
