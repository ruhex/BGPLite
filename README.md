# BGPLite

A BGP-4 route server for dynamic prefix provisioning. Peers register through an HTTP management API, subscribe to AS-lists, country lists, or custom prefixes, and receive them over eBGP. Prefix data comes from RIPEstat and pluggable file/HTTP sources.

BGPLite is not a router: it announces curated prefix sets, it does not forward transit traffic or maintain a full RIB/FIB. The BGP stack (wire codec, session FSM) is implemented in C# directly against RFC 4271, without BIRD/FRR/GoBGP.

Production deployment: `bgp.vhex.dev`.

<p align="center">
  <a href="https://github.com/ruhex/BGPLite/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/ruhex/BGPLite/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/ruhex/BGPLite/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/ruhex/BGPLite/actions/workflows/codeql.yml/badge.svg"></a>
  <a href="https://github.com/ruhex/BGPLite/releases"><img alt="Release" src="https://img.shields.io/github/v/release/ruhex/BGPLite?logo=github"></a>
  <a href="https://github.com/ruhex/BGPLite/pkgs/container/bgplite"><img alt="Docker" src="https://img.shields.io/badge/ghcr.io-bgplite-2496ed?logo=docker"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET" src="https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/ruhex/BGPLite?color=blue"></a>
</p>

## Features

Protocol:

- BGP-4 messages: OPEN / UPDATE / KEEPALIVE / NOTIFICATION; route refresh (RFC 2918, capability-gated, rate-limited)
- 4-octet ASN (RFC 6793): AS_TRANS, AS4_PATH/AS4_AGGREGATOR tunneling for 2-octet-only peers
- Capability negotiation (RFC 5492), Graceful Restart advertisement (RFC 4724)
- Communities (RFC 1997) and Large Communities (RFC 8092): per-peer tagging and outgoing filters
- UPDATE batching (≤100 NLRI) and exact-union CIDR aggregation

Prefix provisioning:

- Provider factory: `http` (any raw-file URL), `file`, and RIPEstat (`stat.ripe.net`), all with in-memory TTL caching and stale-on-failure
- Per-source HTTP timeout and custom headers; extensible via `IPrefixSourceProvider`
- Subscriptions by AS-list or country, custom prefixes, custom ASNs, per-peer user URL sources

Operations:

- HTTP management API for peer/route/session management
- SQLite peer store via EF Core (migrations: `dotnet ef migrations add <Name> --project BGPLite.Api`)
- Docker image on GHCR; self-contained binaries (linux-x64/arm64, win-x64); automated Conventional-Commits releases

## Architecture

```
  operator ──HTTP──► Management API (ApiPort, default 5001) + SQLite peer store
                          │ subscription
  BGP peer ──TCP/179──► BgpServer/BgpSession (FSM, timers)
                          │ resolves per-peer route set
                          ▼
                 Providers (RIPEstat / file / http, TTL cache)
                          │
                 Routing (RouteTable, aggregation, community filter)
                          ▼
                 advertise prefixes via UPDATE
```

Eight projects with enforced one-directional layering — the dependency matrix, layer responsibilities, and data flow are documented in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Quick start

> Port 179 is privileged: binding needs `root` or `CAP_NET_BIND_SERVICE` on Linux. The Docker compose example uses host networking for that reason.

### Docker

```bash
docker pull ghcr.io/ruhex/bgplite:latest

cp appsettings.Example.yml appsettings.yml   # edit before running; secrets stay outside the image
docker run -d --name bgplite \
  --network host \
  -v "$PWD/appsettings.yml:/app/appsettings.yml:ro" \
  -v "$PWD/data:/app/data" \
  ghcr.io/ruhex/bgplite:latest
```

Or with the bundled `docker-compose.yml`:

```bash
cp appsettings.Example.yml appsettings.yml
docker compose up -d
```

### Prebuilt binary

Self-contained single-file binaries are on the [releases](https://github.com/ruhex/BGPLite/releases) page:

```bash
tar xzf bgplite-v1.0.0-linux-x64.tar.gz
cp appsettings.Example.yml appsettings.yml
sudo ./BGPLite                                # port 179 needs root
```

### From source

```bash
git clone https://github.com/ruhex/BGPLite && cd BGPLite
cp appsettings.Example.yml appsettings.yml   # then edit
sudo dotnet run --project BGPLite -c Release
```

Requires the .NET 10 SDK (`global.json` pins the version).

## Configuration

BGPLite reads `appsettings.yml`. The full schema is in [`appsettings.Example.yml`](appsettings.Example.yml); highlights:

```yaml
Bgp:
  Asn: 65444
  RouterId: 10.0.0.1
  KeepAlive: 60
  HoldTime: 180

Peers:
  - Address: 10.0.0.2
    RemoteAsn: 65001
    Description: "example-peer"

RipeStat:                      # ASN → prefixes via stat.ripe.net (cached, retried)
  TimeoutSeconds: 180
  RetryAttempts: 2

PrefixSources:                 # provider factory (Kind: file | http), in-memory TTL cache
  - Kind: http
    Name: ru
    Url: "https://raw.githubusercontent.com/<org>/<repo>/main/ru.txt"
    Timeout: 30
  - Kind: file
    Name: local
    Path: extra.txt
    Community: "65444:100"

DefaultPrefixSource: ru        # served to unconfigured/auto-registered peers
```

- Prefix list files: one CIDR per line (`2.16.20.0/23`); blank lines and `#` comments ignored.
- Peer data lives in SQLite at `$BGPLITE_DATA/bgplite.db` (defaults to `./data`).

## How it works

1. A peer is registered via `POST /api/peers` (IP, ASN, AS-list subscriptions and/or custom prefixes).
2. The peer connects over BGP to port 179.
3. On session establishment the server resolves the peer's subscription:
   - known peer — fetches prefixes for its subscriptions (cached) plus custom prefixes and advertises them;
   - unknown peer — auto-registers it and advertises the default prefix source.
4. Peer status and session timestamps are updated in the store.

## Management API

Listens on `ApiPort` (default **5001**, loopback by default).

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/my-ip` | Returns the caller's IP |
| `POST` | `/api/peers` | Register a peer |
| `GET`  | `/api/peers` | List all peers |
| `GET`  | `/api/asn-lists` | Available AS-lists with prefix counts |
| `GET`  | `/api/as/{asn}/prefixes/count` | Prefix count for an ASN |
| `GET`  | `/api/sessions` | Active BGP session count |
| `GET`  | `/api/routes/count` | Route counts by community |
| `GET` / `PUT` / `DELETE` | `/api/peer/{ip}/communities` | Get / set / clear community filter |
| `PUT`  | `/api/peer/{ip}/description` | Set peer description |

```bash
curl -X POST http://localhost:5001/api/peers -H 'Content-Type: application/json' -d '{
  "ip": "10.0.0.2", "asn": 65001, "description": "customer-1",
  "asnLists": ["cloudflare", "google"], "customPrefixes": ["203.0.113.0/24"]
}'
```

## RFC compliance

A section-by-section audit lives in [`docs/RFC_COMPLIANCE.md`](docs/RFC_COMPLIANCE.md). Summary:

| RFC | Topic | Status |
|-----|-------|--------|
| [4271](https://www.rfc-editor.org/rfc/rfc4271) | BGP-4 (base) | core implemented |
| [4893](https://www.rfc-editor.org/rfc/rfc4893) / [6793](https://www.rfc-editor.org/rfc/rfc6793) | 4-byte ASN, AS4_PATH | implemented |
| [5492](https://www.rfc-editor.org/rfc/rfc5492) | Capabilities | implemented |
| [1997](https://www.rfc-editor.org/rfc/rfc1997) | Communities | implemented |
| [4724](https://www.rfc-editor.org/rfc/rfc4724) | Graceful Restart | advertised; receiving-side retention open (#318) |
| [2918](https://www.rfc-editor.org/rfc/rfc2918) | Route Refresh | implemented |
| [2385](https://www.rfc-editor.org/rfc/rfc2385) | TCP-MD5 auth | open (#36) |
| [4760](https://www.rfc-editor.org/rfc/rfc4760) / [2545](https://www.rfc-editor.org/rfc/rfc2545) | MP-BGP / IPv6 | roadmap (#14/#15) |

Known deliberate deviations are catalogued in [`docs/DESIGN_DECISIONS.md`](docs/DESIGN_DECISIONS.md).

## Roadmap

Work is tracked in [issues](https://github.com/ruhex/BGPLite/issues). IPv6 / MP-BGP is the largest upcoming feature (#14/#15).

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the branch model, commit conventions, and PR expectations. CI runs build, tests, `dotnet format`, and CodeQL on every PR; CodeRabbit posts an automated RFC-aware review.

## License

[MIT](LICENSE) © Mikhail Movchan
