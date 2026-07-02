---
title: "How I Wrote a BGP Server and Didn't Lose My Mind"
published: true
description: "BGPLite — an open-source BGP route server in C# and .NET 10 in roughly 2,500 lines of code. It handles BGP sessions, dynamically loads prefixes via RIPE Stat, and is managed through an HTTP API."
tags: bgp, csharp, dotnet, opensource
---

*Hey, DEV community! 👋 I'm Mikhail, a C# / .NET developer with a passion for networking and infrastructure. This is my first post here — I want to share the story of building [BGPLite](https://github.com/ruhex/BGPLite), an open-source BGP route server in ~2,500 lines of C# that's already running in production.*

*BGPLite accepts BGP sessions, dynamically loads prefixes through RIPE Stat, and is managed via an HTTP API. Source code on [GitHub](https://github.com/ruhex/BGPLite).*

When I first opened [RFC 4271](https://datatracker.ietf.org/doc/html/rfc4271), BGP seemed like some dark magic from the carrier world. A few weeks later, I was debugging my own BGP server, accepting real peerings, and arguing with MikroTik about 4-byte ASNs.

In this article, I'll walk through how BGPLite came to be, what pitfalls I hit along the way, and why writing your own BGP server turned out to be much simpler than it first appeared.

## Table of Contents

1. [Why I Built This](#why-i-built-this)
2. [Why Not BIRD, FRR, or GoBGP](#why-not-bird-frr-or-gobgp)
3. [Why .NET](#why-net)
4. [How BGP Works (Briefly)](#how-bgp-works-briefly)
5. [BGPLite Architecture](#bgplite-architecture)
6. [The Most Interesting Technical Challenges](#the-most-interesting-technical-challenges)
   - [TCP Fragmentation: Why the First Prototype Broke in a Real Network](#tcp-fragmentation-why-the-first-prototype-broke-in-a-real-network)
   - [ASN32: Two Days of Debugging](#asn32-two-days-of-debugging)
   - [Capability Negotiation: Why Mikrotik Kept Tearing Down the Session](#capability-negotiation-why-mikrotik-kept-tearing-down-the-session)
   - [Socket Write Race Condition: The First Production Bug](#socket-write-race-condition-the-first-production-bug)
   - [UPDATE Batching: When 8,000 Prefixes Don't Fit in One Packet](#update-batching-when-8000-prefixes-dont-fit-in-one-packet)
   - [RIPE Stat Caching: 50 Peers → 1 Request](#ripe-stat-caching-50-peers--1-request)
7. [It's Already Running in Production](#its-already-running-in-production)
8. [What's Not There Yet](#whats-not-there-yet)
9. [What's Next](#whats-next)

---

## Why I Built This

Let me start with an honest admission: I wanted to understand BGP from the inside out.

When you work with networks, BGP looks like something monstrous: RFCs spanning dozens of pages, supplementary specifications galore, complex configurations in BIRD, FRR, Cisco, and Juniper. You configure `neighbor X.X.X.X remote-as 65444`, and everything works — but what exactly happens under the hood often remains a mystery.

What messages do routers exchange? How does capability negotiation work? Why does a session sometimes fail to come up? What's behind all those OPEN, KEEPALIVE, and UPDATE packets?

To figure this out not just from documentation but through hands-on practice, I decided to write my own BGP implementation.

In parallel, I had a concrete practical task: I needed a route server that dynamically distributes prefixes to clients. Not a full BGP router, not a policy engine for millions of routes — just "connect via BGP, get your routes, get to work." Moreover, each client has a different set of routes: one wants Cloudflare + Google, another wants prefixes for a specific country, a third wants a custom selection. And all of this should be managed through an HTTP API, because adding a client via CLI in 2026 is a thing of the past.

## Why Not BIRD, FRR, or GoBGP

I looked at the existing tools:

- **BIRD** — a wonderful daemon, but its config is a programming language unto itself. Want to add a client via API? Generate a config, feed it to `birdc configure`, and pray the parser doesn't choke. Dynamic loading of prefixes from external sources? Not out of the box.
- **FRR** (formerly Quagga) — heavyweight, with a legacy architecture. Overkill for a simple task.
- **GoBGP** — powerful but enormous. Figuring out the codebase is harder than writing your own.

The common problem: they're all *full BGP speakers*. They can do everything (policy routing, route reflection, MPLS, EVPN), but for my task, 90% of that is unnecessary.

And then I thought: what if I wrote my own? First, it's the best way to understand a protocol — implement it from scratch. Second, .NET 10 with its `Span<byte>`, async/await, and EF Core is an excellent platform for such a project. Third, ~2,500 lines of code seemed like a feasible weekend project.

Spoiler: it took more than one weekend. But the protocol really did turn out to be simpler than I expected.

## Why .NET

Spoiler: not because it's "trendy." The decision was driven by several factors:

**`Span<byte>` and `Memory<byte>`.** BGP is a binary protocol. Parsing packets on `ReadOnlySpan<byte>` without intermediate allocations is exactly what you need for networking code. In Go, you'd wrestle with `io.Reader` and copies. In Rust, you'd fight the borrow checker (though it would be more idiomatic there). In .NET, it feels natural.

**Async I/O out of the box.** Each BGP session is two parallel coroutines (reading and keepalive). `async/await` + `CancellationToken` is a standard pattern — no manual callbacks or state machines.

**EF Core for storage.** Peers, subscriptions, custom prefixes — a classic relational model. `DbContextFactory` + SQLite is three lines of configuration, and you have a thread-safe data store.

**And honestly — I'm just a C# developer.** I could have written it in Go, Rust, or C. But why, when you know your tool and can do the same thing with it? The best language for a project is the one you know.

---

## How BGP Works (Briefly)

If you've never looked under the hood of BGP — here's everything you need to know to follow this article.

BGP has exactly **4 message types**:

| Type | Code | Purpose |
|------|------|---------|
| OPEN | 1 | Handshake: ASN, Hold Time, capabilities |
| UPDATE | 2 | Announce/withdraw prefixes with attributes |
| NOTIFICATION | 3 | Error message; session closes after this |
| KEEPALIVE | 4 | "I'm alive," confirms OPEN |

Each message starts with a 19-byte header: 16 bytes of marker (`0xFF`), 2 bytes of length, 1 byte of type. The format is strictly binary — no text fields.

**Session establishment** is a finite state machine (FSM). For a route server scenario, a minimal set of states suffices:

```
Idle → Connect → OpenSent → OpenConfirm → Established
```

In Established, the session lives: both sides exchange UPDATE (routes) and KEEPALIVE (liveness confirmation). If the HOLD Timer expires or a NOTIFICATION arrives — the session is torn down.

**UPDATE** is the most complex message. It contains three sections:
- Withdrawn Routes — prefixes the peer is withdrawing
- Path Attributes — route metadata (Origin, AS_PATH, Next Hop, Communities)
- NLRI (Network Layer Reachability Information) — announced prefixes

Prefixes are encoded compactly: only significant bytes. `192.168.0.0/16` is 3 bytes, not 5. `/24` is 4 bytes. `/0` is 1 byte (length only).

Everything else (capabilities, communities, MP-BGP) is optional extensions on top of this foundation. Basic BGP is remarkably simple.

---

## BGPLite Architecture

```
  Client (BIRD/Cisco/Mikrotik)
         │
         │ BGP (TCP :179)
         ▼
┌─────────────────┐    ┌──────────────────┐
│   BGP Server    │    │    HTTP API      │
│  (BgpSession)   │    │  (:5000)         │
│  (BgpMetrics)   │    │                  │
└────────┬────────┘    └────────┬─────────┘
         │                      │
         ▼                      ▼
┌─────────────────────────────────────────┐
│              Peer Store                 │
│         (EF Core + SQLite)              │
│  Peer → Subscription → CustomPrefix     │
│  Peer → Community                       │
└────────────────┬────────────────────────┘
                 │
         ┌───────┴───────┐
         ▼               ▼
┌────────────────┐ ┌──────────────┐
│  Route Table   │ │  Prefix      │
│  (Community    │ │  Service     │
│   Filters)     │ │  (Cache)     │
└────────────────┘ └──────┬───────┘
                          │
                   ┌──────┴───────┐
                   ▼              ▼
            ┌────────────┐ ┌───────────┐
            │ RIPE Stat  │ │ nets.txt  │
            │ API        │ │ (local)   │
            └────────────┘ └───────────┘
```

The project is split into 7 domain modules:

```
BGPLite/               # Entry point, DI, host setup
BGPLite.Protocol/      # BGP message encoding/decoding (zero dependencies)
BGPLite.Server/        # TCP listener, BGP session FSM
BGPLite.Routing/       # Route table, community filters
BGPLite.Providers/     # RIPE Stat client, cache, local prefixes
BGPLite.Api/           # HTTP API, EF Core, PeerStore
BGPLite.Configuration/ # YAML config
```

`Protocol` knows nothing about networking. `Server` knows nothing about the database. `Routing` knows nothing about HTTP. A clean modular monolith without over-engineering.

The peer data model:

```cs
public class Peer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Ip { get; set; } = "";
    public uint? Asn { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "inactive";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSessionAt { get; set; }

    public List<PeerCommunity> Communities { get; set; } = [];
    public List<PeerSubscription> Subscriptions { get; set; } = [];
    public List<PeerCustomPrefix> CustomPrefixes { get; set; } = []
}
```

When a peer connects via BGP, the server looks it up in this table by IP address and determines which routes to send. A peer has three types of relationships: community filters (which routes to receive), AS-list subscriptions (where to source them from), and custom prefixes (additional ones).

---

## The Most Interesting Technical Challenges

### TCP Fragmentation: Why the First Prototype Broke in a Real Network

I wrote the first prototype in one evening. Open a TCP socket, read data via `NetworkStream.ReadAsync()`, parse a BGP message. With BIRD on localhost — everything worked perfectly.

Then I connected a real router over the network. And everything broke.

`ReadAsync` doesn't guarantee that you'll read exactly one BGP packet. TCP is a stream protocol. A single `ReadAsync` call can return:
- Half a message (the TCP segment didn't arrive in full)
- One and a half messages (two packets got glued together in the buffer)
- 0 bytes (connection closed)

My naïve code:

```cs
// DON'T DO THIS
var buffer = new byte[4096];
var read = await stream.ReadAsync(buffer, ct);
var message = BgpMessageReader.ReadMessage(buffer.AsSpan(0, read));
```

This worked on localhost because packets almost never get fragmented there. But over a real network — random parser crashes with cryptic "Invalid BGP marker" or "Message too short" errors.

I had to write `ReadExactAsync`, which guarantees reading an exact number of bytes:

```cs
private async Task ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
{
    var totalRead = 0;
    while (totalRead < buffer.Length)
    {
        var read = await _stream.ReadAsync(buffer[totalRead..], ct);
        if (read == 0)
            throw new IOException("Connection closed by peer");
        totalRead += read;
    }
}
```

And read in two passes: first the 19-byte header (to learn the full length), then the remainder. This is also where `ArrayPool<byte>` came in handy — instead of `new byte[4096]` for every message, we rent a buffer from the pool. With dozens of concurrent sessions, the difference in GC pressure is noticeable.

### ASN32: Two Days of Debugging

I thought an ASN was just a 16-bit number in the OPEN packet. Initial tests with BIRD (ASN 65444) passed. Then I tried connecting a client with ASN 397143 — the session wouldn't establish.

I spent two days looking for the problem. The parsing was correct, the bytes were right, but the peer was receiving some other ASN. It turned out that the ASN field in the OPEN packet is 16 bits, while the modern internet uses 4-byte ASNs (AS > 65535). Values > 65535 simply don't fit.

The solution is described in [RFC 4893](https://datatracker.ietf.org/doc/html/rfc4893): a capability with code 65 (Four-Octet ASN), while the 16-bit field gets `AS_TRANS` (23456) — a reserved value meaning "look in the capabilities":

```cs
var asn16 = _bgpConfig.Asn > ushort.MaxValue ? (ushort)23456 : (ushort)_bgpConfig.Asn;
```

When reading, you do the reverse: first check capabilities, and only if capability 65 is absent — take the 16-bit field. This is a classic backward-compatible pattern from the RFC.

But the story didn't end there. I implemented sending the 4-byte ASN in my OPEN, but forgot to *parse* it when reading the peer's OPEN. A client with ASN 397143 would connect, and the server saw ASN 23456. The result: another half a day of debugging.

### Capability Negotiation: Why Mikrotik Kept Tearing Down the Session

When you have only one type of client — BIRD on Linux — everything works. But in the real world, different devices connect to a route server: BIRD, FRR, Mikrotik RouterOS, Cisco IOS, Juniper JunOS. And each has its own BGP "dialect."

The first problem surfaced with Mikrotik. The session wouldn't come up — Mikrotik was sending a NOTIFICATION error right after my OPEN. A packet dump showed: Mikrotik wasn't sending the MP-BGP (Multiprotocol Extensions) capability, while my server was replying with MP-BGP IPv4/Unicast. Mikrotik didn't expect that and tore down the session.

I had to implement capability adaptation: our OPEN contains only the capabilities the peer supports:

```cs
var capabilities = new List<BgpCapabilityInfo>
{
    BgpCapabilityInfo.FourOctetAsn(_bgpConfig.Asn)
};

// Only if the peer supports MP IPv4/Unicast
if (PeerHasMpIpv4Unicast(remoteOpen.Capabilities))
    capabilities.Add(BgpCapabilityInfo.MultiprotocolIpv4Unicast());

// Only if the peer supports Route Refresh
if (remoteOpen.Capabilities.Any(c => c.Code == BgpConstants.Capability.RouteRefresh))
    capabilities.Add(BgpCapabilityInfo.RouteRefresh());
```

The takeaway: capability negotiation isn't an optional feature — it's a critically important part of BGP. Without it, real-world devices will refuse to establish a session.

### Socket Write Race Condition: The First Production Bug

Each BGP session has two concurrent tasks: `ReadLoopAsync` (reading) and `KeepAliveLoopAsync` (sending keepalives). When I added `RefreshRoutesAsync` — a method for updating routes when subscriptions change via the API — two socket writes started executing simultaneously.

Two `WriteAsync` calls on one `NetworkStream` at the same time. The TCP buffer contains fragments of two BGP messages glued together. The peer receives garbage and closes the session.

BGP is not a framing protocol. There are no delimiters between messages. The length is determined from the header, and if the bytes of two messages get interleaved — the peer can't parse either one.

The solution — `SemaphoreSlim(1, 1)` to serialize all writes:

```cs
private readonly SemaphoreSlim _sendLock = new(1, 1);

public async Task RefreshRoutesAsync()
{
    if (!IsEstablished) return;

    await _sendLock.WaitAsync();
    try
    {
        await WithdrawAllAsync();
        await SendAllRoutesAsync();
    }
    finally
    {
        _sendLock.Release();
    }
}
```

Why not `lock`? Because `lock` doesn't work with `async`. Why not `Monitor.Enter`? Same reason. `SemaphoreSlim` is the only primitive in .NET that works correctly with async/await.

### UPDATE Batching: When 8,000 Prefixes Don't Fit in One Packet

When the first client subscribed to a large prefix list, the server tried to send ~8,000 prefixes in a single UPDATE message. The peer silently ignored this message — and the client didn't receive a single route.

The maximum BGP message size is 4,096 bytes ([RFC 4271, §4.1](https://datatracker.ietf.org/doc/html/rfc4271#section-4.1)). One UPDATE fits ~120–130 prefixes, depending on the size of path attributes.

The solution — batching by 100:

```cs
const int maxNlriPerUpdate = 100;
foreach (var route in routes)
{
    batch.Add(route);
    if (batch.Count >= maxNlriPerUpdate)
    {
        await SendRouteBatchAsync(nextHop, batch);
        batch.Clear();
    }
}
```

Each batch is a separate UPDATE with a full set of path attributes (Origin, AS_PATH, Next Hop). This is redundant (we repeat attributes), but guarantees that each message is valid on its own.

### RIPE Stat Caching: 50 Peers → 1 Request

RIPE NCC provides a free API that returns the prefixes announced by a specific ASN:

```
GET https://stat.ripe.net/data/ris-prefixes/data.json?resource=AS13335&list_prefixes=true
```

The problem: RIPE has rate limits, and large ASes (Cloudflare, Google) have hundreds or thousands of prefixes. If 50 peers are subscribed to Cloudflare, you can't make 50 requests to RIPE Stat.

The solution — a 1-hour cache in a `ConcurrentDictionary`:

```cs
private readonly ConcurrentDictionary<uint, (IReadOnlyList<(uint, byte)> Data, DateTime CachedAt)> _cache = new();
private readonly TimeSpan _cacheTtl = TimeSpan.FromHours(1);
```

The first request to AS13335 goes to RIPE Stat, the next 49 are served from cache. After an hour — it refreshes on the next access.

For local prefixes (e.g., a specific country's networks), a `nets.txt` file is used — loaded once at startup, with no external requests.

---

## It's Already Running in Production

The project is already deployed on a real server and serving several BGP peers: **[bgp.vhex.dev](https://bgp.vhex.dev/)**

### HTTP API

The API is built on `HttpListener` — without ASP.NET Core. Why pull in Kestrel when you only need routing for 10 endpoints?

A notable feature of `GET /api/server` — it generates ready-made configs for BIRD, Cisco IOS, and Mikrotik RouterOS with pre-filled ASN and Router ID. The client just needs to copy-paste and insert their own IP:

```json
{
  "bird": [
    "protocol bgp bgplite {",
    "  local as <YOUR_ASN>;",
    "  neighbor 10.0.0.1 as 65444;",
    "  multihop;",
    "  hold time 180;",
    "  ipv4 {",
    "    import filter bgplite_in;",
    "    export none;",
    "  };",
    "}"
  ]
}
```

Source code: [github.com/ruhex/BGPLite](https://github.com/ruhex/BGPLite).

---

## What's Not There Yet

I'll be honest — BGPLite doesn't claim to replace BIRD or FRR. Here's what's not implemented yet:

- **IPv6 / MP-BGP** — IPv4 unicast only
- **RPKI validation** — prefixes aren't checked for validity
- **BGP MD5 Authentication** — no session spoofing protection
- **Route Refresh** — updating prefixes requires withdrawing and resending all routes
- **Graceful Restart** — when the server goes down, peers lose all routes
- **BFD** — no fast link-failure detection (only BGP keepalive)
- **BMP** — no BGP session monitoring
- **Full Table** — not designed to receive full internet routing tables (~900K prefixes)

These are deliberate limitations. BGPLite solves a specific problem — personal prefix distribution via subscriptions. For full BGP routing, use BIRD or FRR. That said, some items from this list will appear in future versions — depending on community interest.

---

## What's Next

BGPLite will continue to evolve if the community finds the project interesting. Priorities:
- Route Refresh (soft reconfig without session teardown)
- RPKI prefix validation
- MP-BGP for IPv6
- Extracting `BGPLite.Protocol` into a standalone NuGet package — so the BGP protocol implementation can be reused in other projects without tying them to the route server
- More data sources: alongside RIPE Stat, add Hurricane Electric (HE) BGP Toolkit and others
- Exclusion rules for prefixes — the ability to specify prefixes you don't want to receive, even if they're in your subscriptions
- Prometheus/Grafana metrics
- Web UI for peer management

Nowadays, the advancement of networking technologies is more important than ever. CDN, edge computing, multi-cloud, geographically distributed systems — all of these require an understanding of routing. BGP is not vendor magic — it's an understandable protocol that you can implement over a weekend.

If you find this topic interesting — come join in: [github.com/ruhex/BGPLite](https://github.com/ruhex/BGPLite). Stars, issues, and pull requests are all welcome.
