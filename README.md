<p align="center">
  <h1 align="center">🛰️ BGPLite</h1>
  <p align="center"><em>A lightweight, hand-rolled BGP-4 route server for dynamic prefix provisioning.</em></p>
  <p align="center">Peers register via HTTP, subscribe to AS-lists / country lists / custom prefixes, and receive them over eBGP — powered by RIPEstat and pluggable prefix sources.</em></p>
</p>

<p align="center">
  <a href="https://github.com/ruhex/BGPLite/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/ruhex/BGPLite/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/ruhex/BGPLite/actions/workflows/codeql.yml"><img alt="CodeQL" src="https://github.com/ruhex/BGPLite/actions/workflows/codeql.yml/badge.svg"></a>
  <a href="https://github.com/ruhex/BGPLite/releases"><img alt="Release" src="https://img.shields.io/github/v/release/ruhex/BGPLite?logo=github"></a>
  <a href="https://github.com/ruhex/BGPLite/pkgs/container/bgplite"><img alt="Docker" src="https://img.shields.io/badge/ghcr.io-bgplite-2496ed?logo=docker"></a>
  <a href="https://dotnet.microsoft.com/"><img alt=".NET" src="https://img.shields.io/badge/.NET-10-512bd4?logo=dotnet"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/github/license/ruhex/BGPLite?color=blue"></a>
</p>

<p align="center">
  <a href="https://github.com/ruhex/BGPLite/stargazers"><img alt="stars" src="https://img.shields.io/github/stars/ruhex/BGPLite?style=flat"></a>
  <a href="#"><img alt="commits: conventional" src="https://img.shields.io/badge/commits-conventional-fe7d37?logo=semantic-release"></a>
  <a href="#"><img alt="CodeRabbit" src="https://img.shields.io/badge/review-CodeRabbit-6c43f5?logo=githubactions"></a>
  <a href="https://github.com/ruhex/BGPLite/issues"><img alt="issues" src="https://img.shields.io/github/issues/ruhex/BGPLite"></a>
</p>

---

## 📑 Table of Contents

- [What is BGPLite?](#-what-is-bgplite)
- [Features](#-features)
- [Architecture](#-architecture)
- [Quick Start](#-quick-start)
- [Configuration](#-configuration)
- [How it works](#-how-it-works)
- [Management API](#-management-api)
- [RFC compliance](#-rfc-compliance)
- [Roadmap](#-roadmap)
- [Contributing](#-contributing)
- [License](#-license)

## 🧭 What is BGPLite?

**BGPLite** is a BGP-4 **route server**: it accepts eBGP sessions from clients and **dynamically advertises curated prefix sets** to them — by ASN (Cloudflare, Google, Apple, Meta…), by country, or fully custom. It is **not** a full router: it originates/announces curated prefixes, it does not forward transit traffic or run a full RIB/FIB.

Clients register a subscription through the HTTP management API; on the next BGP connection the server resolves the prefixes (RIPEstat + file/http sources, cached) and announces them. Unknown peers are auto-registered with a default set (e.g. RU).

- **Production-deployed** at `bgp.vhex.dev` (real BGP peers).
- **Pure .NET 10**, raw TCP/179 sockets (no BIRD/FRR/GoBGP) — the BGP stack is written from scratch per RFC 4271.

## ✨ Features

- **Full BGP-4 message support** — OPEN / UPDATE / KEEPALIVE / NOTIFICATION.
- **4-byte ASN** (RFC 4893/6793) — `AS_TRANS` 23456, capability 65, AS4_PATH for 2-byte-only peers.
- **Capability negotiation** (RFC 5492) — tuned for BIRD / FRR / Mikrotik / Cisco / Juniper.
- **Graceful Restart** (RFC 4724).
- **Route Refresh** (RFC 2918) — capability-gated, DoS-debounced.
- **BGP Communities** (RFC 1997) — per-peer filtering and tagging (`ASN:VALUE`).
- **UPDATE batching** (≤100 NLRI) and **exact-union CIDR aggregation**.
- **Prefix provisioning** via a pluggable provider factory:
  - `http` (any raw-file URL), `file`, and **RIPEstat** (`stat.ripe.net`) with in-memory TTL caching.
  - Per-source HTTP timeout, custom headers, and community tagging.
  - Extend with your own `IPrefixSourceProvider`.
- **HTTP management API** for peer/route/session management.
- **SQLite peer store** via EF Core (`EnsureCreated`, raw-SQL migrations — see [FIXPLAN](FIXPLAN.md) P4).
- **First-class ops**: Docker image in GHCR, self-contained binaries (linux-x64/arm64, win-x64), Conventional-Commits releases.

## 🏗 Architecture

```
            ┌──────────────── HTTP :5000 (management API + SQLite peer store) ──────────────┐
            │                                                                                │
  client ───┤   POST /api/peers (subscribe: asnLists / customPrefixes / communities)        │
            │                                                                                │
            └────────────────────────────────────────────────────────────────────────────────┘
                                            │  on connect
  BGP peer ──TCP/179──► BGPLite.Server (session FSM, timers, hold-timer, Cease)
                                │ resolves subscription
                                ▼
                     BGPLite.Providers (RIPEstat / file / http  →  TTL cache)
                                │ BGPLite.Routing (RouteTable, aggregation, community filter)
                                ▼
                          advertise prefixes via UPDATE
```

Solution layout (8 projects, ~6k LOC):

| Project | Responsibility |
|---------|----------------|
| `BGPLite` | Entry point, host setup, DI |
| `BGPLite.Protocol` | BGP wire codec (messages, FSM, capabilities, path attributes) |
| `BGPLite.Server` | TCP listener, session FSM, timers, Cease/teardown |
| `BGPLite.Routing` | Route table, community filters, CIDR aggregation |
| `BGPLite.Providers` | PrefixService, RIPEstat, prefix-source providers + factory |
| `BGPLite.Api` | Management HTTP API + SQLite peer store (EF Core) |
| `BGPLite.Configuration` | YAML config loading, `AppConfig` models |
| `BGPLite.Tests` | xUnit unit tests |

## 🚀 Quick Start

> **Port 179 < 1024**: bind needs `root` or `CAP_NET_BIND_SERVICE` (Linux). The Docker compose example uses host networking to make peering straightforward.

### Option A — Docker (recommended)

```bash
# Pull the official image from GHCR
docker pull ghcr.io/ruhex/bgplite:latest

# Configure (secrets stay OUT of the image — mount from host)
cp appsettings.Example.yml appsettings.yml
$EDITOR appsettings.yml

# Run (host networking for BGP; or map ports + add NET_BIND_SERVICE)
docker run -d --name bgplite \
  --network host \
  -v "$PWD/appsettings.yml:/app/appsettings.yml:ro" \
  -v "$PWD/data:/app/data" \
  ghcr.io/ruhex/bgplite:latest
```

…or with the bundled `docker-compose.yml`:

```bash
cp appsettings.Example.yml appsettings.yml
docker compose up -d
```

### Option B — Prebuilt binary (release)

Grab a self-contained single-file binary from [Releases](https://github.com/ruhex/BGPLite/releases):

```bash
tar xzf bgplite-v1.0.0-linux-x64.tar.gz
cp appsettings.Example.yml appsettings.yml   # template included in the archive
sudo ./BGPLite                                # port 179 needs root
```

### Option C — Build from source

```bash
git clone https://github.com/ruhex/BGPLite && cd BGPLite
cp appsettings.Example.yml appsettings.yml   # then edit
sudo dotnet run --project BGPLite -c Release
```

Requires the **.NET 10 SDK** (`global.json` pins it).

## ⚙️ Configuration

BGPLite reads `appsettings.yml` (YAML). See [`appsettings.Example.yml`](appsettings.Example.yml) for the full schema. Highlights:

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

RipeStat:                      # resolves ASN → prefixes via stat.ripe.net (cached, retried)
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

## 🔁 How it works

1. **Register a peer** via `POST /api/peers` (IP, ASN, AS-list subscriptions and/or custom prefixes).
2. **Peer connects** over BGP to port 179.
3. **Session establishes** — the server looks the peer up:
   - **known** → fetches prefixes for its subscriptions (RIPEstat, cached) + custom prefixes, advertises all;
   - **unknown** → auto-registers and advertises the **default** prefix source.
4. **Stats updated** — peer status `active`, session time recorded.

## 🌐 Management API

Available on port **5000**.

### Peers
| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/my-ip` | Returns the caller's IP |
| `POST` | `/api/peers` | Register a peer |
| `GET`  | `/api/peers` | List all peers |

```bash
curl -X POST http://localhost:5000/api/peers -H 'Content-Type: application/json' -d '{
  "ip": "10.0.0.2", "asn": 65001, "description": "customer-1",
  "asnLists": ["cloudflare", "google"], "customPrefixes": ["203.0.113.0/24"]
}'
```

### AS-lists / routes / sessions
| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET`  | `/api/asn-lists` | Available AS-lists with prefix counts |
| `GET`  | `/api/as/{asn}/prefixes/count` | Prefix count for an ASN |
| `GET`  | `/api/sessions` | Active BGP session count |
| `GET`  | `/api/routes/count` | Route counts by community |

### Per-peer community filter & metadata
| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` / `PUT` / `DELETE` | `/api/peer/{ip}/communities` | Get / set / clear community filter |
| `PUT` | `/api/peer/{ip}/description` | Set peer description |

## 📜 RFC compliance

A §-by-§ audit lives in [`RFC_COMPLIANCE.md`](RFC_COMPLIANCE.md). Summary:

| RFC | Topic | Status |
|-----|-------|--------|
| [4271](https://www.rfc-editor.org/rfc/rfc4271) | BGP-4 (base) | ✅ core |
| [4893](https://www.rfc-editor.org/rfc/rfc4893) / [6793](https://www.rfc-editor.org/rfc/rfc6793) | 4-byte ASN, AS4_PATH | ✅ |
| [5492](https://www.rfc-editor.org/rfc/rfc5492) | Capabilities | ✅ |
| [1997](https://www.rfc-editor.org/rfc/rfc1997) | Communities | ✅ |
| [4724](https://www.rfc-editor.org/rfc/rfc4724) | Graceful Restart | ✅ |
| [2918](https://www.rfc-editor.org/rfc/rfc2918) | Route Refresh | ✅ |
| [2385](https://www.rfc-editor.org/rfc/rfc2385) | TCP-MD5 auth | 🟡 open (#36) |
| [4760](https://www.rfc-editor.org/rfc/rfc4760) / [2545](https://www.rfc-editor.org/rfc/rfc2545) | MP-BGP / IPv6 | 🔜 roadmap (#14/#15) |

## 🗺 Roadmap

Prioritized work is tracked in [`FIXPLAN.md`](FIXPLAN.md) (P1–P7) and via [issues](https://github.com/ruhex/BGPLite/issues). Currently open focus areas: routing correctness (P3), configuration validation (P4), test coverage (P5), and the remaining RFC compliance gaps (P7). IPv6 / MP-BGP is the largest upcoming feature (#14/#15).

## 🤝 Contributing

Contributions are welcome. To keep history clean and releases automatic:

- Use **Conventional Commits** (`feat:`, `fix:`, `docs:`, `chore:`…) — [release-please](https://github.com/googleapis/release-please) derives versions & changelogs from them.
- Open a **feature branch → PR**; CI runs build + tests + `dotnet format` + CodeQL on every PR.
- [CodeRabbit](https://coderabbit.ai) posts an automated review on each PR (`.coderabbit.yaml` configures BGP/RFC-aware focus).
- Squash-merge into `main`; releases tag & publish themselves.

## 📄 License

[MIT](LICENSE) © Mikhail Movchan
