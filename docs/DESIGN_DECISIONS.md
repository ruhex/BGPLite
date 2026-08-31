# BGPLite Design Decisions

Catalog of deliberate decisions and known RFC deviations — the "why" behind behaviors that may
look wrong to a reader (or an agent) encountering them in code. Each entry: decision, context,
consequence, and where it is tracked. Verified against `main @ 64ce3a6`.

When closing or changing one of these, update this file and the tracker reference together.
For section-by-section RFC conformance status, see `RFC_COMPLIANCE.md` (2026-07-02 snapshot).

## Protocol

### D1. Passive-only FSM — no `Active` state
- **Decision:** the FSM implements Idle → Connect → OpenSent → OpenConfirm → Established; the RFC 4271
  §8 `Active` state and ConnectRetryTimer are omitted.
- **Context:** BGPLite is a passive listener only; there is no outbound-connect path.
- **Consequence:** state naming/logging differs from the full RFC FSM. Reintroducing `Active` belongs
  with outbound connect support, not as a drive-by.
- **Tracker:** #10 item 1.

### D2. RFC 7606 §3(j) deviation — unparseable NLRI discards the frame, session survives
- **Decision:** an UPDATE whose NLRI field cannot be parsed is discarded and the session stays up,
  where RFC 7606 §3(j) prescribes session reset.
- **Context:** resetting on a single malformed frame is a remote session-kill lever (#222/#284).
  Framing is length-driven, so the stream cannot desync from this discard.
- **Consequence:** the routes in that one UPDATE cannot be withdrawn (their NLRI is unknown); the
  deviation is recorded in `ReadLoopAsync` and here.
- **Tracker:** documented in code (#222); intentionally kept.

### D3. Malformed AGGREGATOR/AS4_AGGREGATOR takes attribute discard (RESOLVED)
- **Decision:** a malformed AGGREGATOR or AS4_AGGREGATOR (length ≠ 6/8 by session type, or AS 0 per
  RFC 7607) is DISCARDED per RFC 7606 §7.7 (AGGREGATOR) / RFC 6793 §6 (AS4_AGGREGATOR — RFC 7606 §7.8
  is COMMUNITY and §7 excludes attribute 18) — the attribute is dropped, the UPDATE's routes stay,
  a Warning names the dropped type codes.
- **Context:** was temporarily treat-as-withdraw (stricter than the RFC — routes a conformant
  implementation installs were lost) until the attribute-discard mechanism existed (#306). Flags
  conflicts on these attributes REMAIN treat-as-withdraw per RFC 7606 §3 — discard is reserved for
  value/length malformation of attributes with no route-selection effect (§2).
- **Consequence:** the deferred RFC 7607 half (AS 0 in both aggregator attributes) landed with the
  mechanism; ValidateAggregatorReconstruction tolerates a discarded AGGREGATOR so a carried-but-malformed
  one does not trip the pairing rule.
- **Tracker:** #306.

### D4. Per-type minimum message lengths classified as body errors
- **Decision:** too-short OPEN/UPDATE/NOTIFICATION/ROUTE_REFRESH frames surface as Open/Update
  Message Error from the body parsers, not as §6.1 Bad Message Length.
- **Context:** UPDATE-as-body-error routes to treat-as-withdraw and keeps the session; a header error
  would tear it down — a remote-kill regression (#222/#284/#300). KEEPALIVE length IS checked as
  Bad Message Length per §6.1.
- **Consequence:** NOTIFICATION/ROUTE_REFRESH sub-minimal frames report `1/0` instead of `1/2`+data.
- **Tracker:** #300 (closed with this decision); residual subcode nuance in #292 item 4 discussion.

### D5. AS_TRANS inside a received AS4_PATH is rejected
- **Decision:** `ReadAs4Path` rejects AS_TRANS (23456) inside AS4_PATH as Malformed AS_PATH.
- **Context:** defensive over-rejection; RFC 6793 contains no explicit prohibition (noted in the
  code itself).
- **Consequence:** an UPDATE a conformant peer might accept is treated-as-withdraw.
- **Tracker:** #238 (deliberate, kept).

### D6. Graceful Restart advertised without receiving-side retention
- **Decision:** the GR capability (with IPv4/Unicast tuple) is advertised by default, but routes of a
  silently-disconnecting GR peer are flushed immediately; no stale-marking, no Restart-Time timer,
  no receive-side EoR handling.
- **Context:** RFC 4724 §4.2 requires the receiving speaker to retain and mark stale; implementing
  the full receiving half was deferred.
- **Consequence:** the capability promises behavior the code does not have; peer routes flap during
  peer restarts. Either stop advertising or implement retention.
- **Tracker:** #318 (open).

### D7. Hold time semantics
- **Decision:** negotiated hold time = `min(local, peer)` with 0 on either side disabling timers;
  1–2 s rejected (Unacceptable Hold Time); keepalive interval = `max(negotiatedHoldTime/3, 1)` —
  the configured `KeepAlive` is validated against HoldTime/3 but does not set the wire interval;
  OpenConfirm is bounded by a 4-minute fallback hold when negotiated hold is 0.
- **Context:** RFC 4271 §4.2/§6.2/§6.5; either-side-zero = disabled follows common vendor practice
  (#224); the OpenConfirm bound closes the "handshake-then-silence" pin (#286).
- **Consequence:** sessions can legitimately run without timers; a configured KeepAlive value has no
  direct wire effect.
- **Tracker:** #224, #286 (closed).

## Session and server

### D8. Exactly one NOTIFICATION per teardown (`_teardownReason` CAS latch)
- **Decision:** teardown reasons (LocalCease / RemoteNotification / HoldTimerExpired / SilentClose)
  are latched atomically; every send path CAS-claims the reason before sending, so at most one
  NOTIFICATION is emitted and a peer NOTIFICATION is never replied to.
- **Context:** RFC 4271 §6.3/§8.1; races between read loop, hold-timer loop, refresh, and external
  shutdown would otherwise double-send.
- **Consequence:** silent-close paths (GR-aware shutdown, session replacement) deliberately emit nothing.
- **Tracker:** evolved through #217/#252/#285; enforced in `BgpSession`.

### D9. No global max-active session cap; per-source-IP accept throttle instead
- **Decision:** there is deliberately no cap on concurrent sessions. Floods from one IP are bounded
  by `Bgp.MaxAcceptsPerIpPerMinute` (rolling 60 s window, socket closed before a session spawns).
- **Context:** a route server is designed to hold many peers — session count is capacity/business
  logic, not a security control; the OS firewall is the primary gate.
- **Consequence:** total session count is bounded only by OS resources; reintroducing a global cap is
  a product decision.
- **Tracker:** #115 (throttle), #265 item 4 (documented omission).

### D10. Send-fault latch: an aborted write poisons the connection
- **Decision:** `SocketBgpConnection` latches a fault after any aborted/cancelled write and fails all
  subsequent writes fast; `RefreshCycleAsync` tears the session down on outbound IOException.
- **Context:** an aborted socket write is partially delivered — the peer is left mid-frame, so
  appending frames only deepens stream corruption (#285/#252).
- **Consequence:** a peer that stops reading kills its own session after the per-send budget (60 s
  default) rather than pinning the send lock for the TCP retransmission timeout (~15 min).
- **Tracker:** #252, #285 (closed).

### D11. Auto-registration of unknown peers
- **Decision:** any peer that completes a valid OPEN exchange is upserted into the peer store and
  served the default (RU) prefix set.
- **Context:** route-server UX — peers appear in the management UI on first connect, no pre-registration.
- **Consequence:** any Internet scanner that speaks BGP creates a persisted peer row; combines with
  the missing max-prefix limit (#304) for unbounded inbound growth.
- **Tracker:** deliberate; inbound bound tracked in #304.

## Routing

### D12. One shared `RouteTable` with per-entry ownership, not per-peer Adj-RIBs-In
- **Decision:** a single table keyed by (prefix, length); each entry records the installing session.
  Withdrawals and the session-close flush are compare-and-remove against the owner.
- **Context:** RFC 4271 §3.2 models per-peer Adj-RIBs-In; BGPLite approximates the withdrawal
  semantics with ownership instead (#289), then flushes owned routes on session end (#313/#314).
- **Consequence:** a later announcement for the same prefix replaces the earlier owner's entry; the
  loser cannot withdraw what it no longer owns. Seeded routes (owner null) cannot be withdrawn by peers.
- **Tracker:** #289, #303, #313 (closed).

### D13. `SharedTableRouteAssembler` is the explicit degraded mode
- **Decision:** without per-peer configuration the fallback serves only the unowned startup seed
  (never peer-injected routes) and logs its activation exactly once at Warning.
- **Context:** #307 — the implicit fallback used to re-advertise one peer's inbound routes to every
  other peer (tenant isolation failure); #263 made the production assembler's dependencies required
  so the fallback is unreachable in a correct composition.
- **Consequence:** reaching it in production means a wiring error; it exists as a named type for
  tests and deliberate minimal compositions.
- **Tracker:** #263, #307 (closed).

### D14. Route refresh debounce and coalescing
- **Decision:** peer ROUTE_REFRESH is rate-limited to one per second per session (CAS), and refresh
  cycles coalesce (one in-flight + one pending lap); the refresh runs off the read loop.
- **Context:** a refresh is a full withdraw + re-announce; awaiting it inline starved KEEPALIVE reads
  and false-fired the hold timer (#253); floods otherwise force N full dumps (#254).
- **Consequence:** a peer can still request one full re-advertise per second — bounded, not free.
- **Tracker:** #253, #254 (closed).

### D15. Outbound attributes are per-community-set cached and type-code ordered
- **Decision:** within one send, path attributes are built once per distinct community set and reused
  across 100-NLRI batches; the writer emits attributes in ascending type-code order (ORIGIN, AS_PATH,
  NEXT_HOP, COMMUNITY, AS4_PATH, LARGE_COMMUNITY).
- **Context:** attribute bytes are identical across batches of a send (#87); ascending order is an
  implementation invariant (RFC 4271 does not require attribute ordering) and the writer sorts
  stably to guarantee it for any producer (#272).
- **Consequence:** attribute order is a convention, not a wire requirement — parsers must not rely on it.
- **Tracker:** #87, #272 (closed).

### D16. Peer deletion sends Cease even with Graceful Restart enabled
- **Decision:** `ISessionManager.TerminatePeerAsync` (management-API peer deletion) sends a Cease
  (Administrative Reset) to the peer's established sessions even when Graceful Restart is enabled —
  unlike `StopAsync`, which silent-closes under GR.
- **Context:** GR retention exists so peers keep our routes across a restart we will return from
  (RFC 4724 §4). Deleting a peer is a permanent removal, not a restart — retention would leave stale
  routes at the peer until its restart timer expired, so the NOTIFICATION termination (which bypasses
  GR and makes the peer flush) is the correct signal (#323).
- **Consequence:** deletion is not a revocation: nothing stops the peer from reconnecting, and D11
  auto-registration serves it again on the next OPEN (a deny-list is a separate product decision).
  Pre-OPEN connections (remote ASN not yet known) are not matched by the (ip, asn) termination and
  may re-register the row once their OPEN arrives.
- **Tracker:** #323.

### D17. UPDATE body errors keep the session alive across BOTH reject classes
- **Decision:** a malformed inbound UPDATE never tears down the session, in both reject classes:
  (a) a frame-level `BgpParseException` with an Update Message Error code (body unparseable, e.g.
  truncated NLRI — the D2 case), and (b) a `BgpNotificationException(3, …)` from the attribute
  pipeline (malformed/missing mandatory attributes, AS4 reconstruction — and unrecognized
  **well-known** attributes; duplicate attributes are NOT a rejection: per RFC 7606 §3(g) later
  occurrences are discarded, the first one keeps processing). The message is rejected (`UpdateRejected`), the session
  stays Established, and no NOTIFICATION is sent.
- **Context:** RFC baseline — RFC 4271 §6.3 prescribes NOTIFICATION + session reset for these
  errors, and RFC 7606 revises only part of the space toward treat-as-withdraw: malformed/missing
  attributes become treat-as-withdraw, but **unrecognized well-known stays session-reset** (7606
  does not revise it; verified against the RFC text 2026-08-30) and §3(j) mandates session reset
  when the NLRI itself cannot be parsed. BGPLite deviates deliberately in both places: a route
  server must not lose a long-lived session — and every other peer's view of it — over one bad or
  adversarial UPDATE (#94, #222, #284 lineage). No NOTIFICATION is sent because RFC 4271 §6.3
  requires its receiver to tear down, defeating the purpose. Withdrawal semantics: class (b)
  withdraws the peer's own NLRI (RFC 7606's "treat" half, #288); class (a) cannot — the NLRI is
  unrecoverable — and discards only (D2).
- **Consequence:** a peer sending structurally valid but semantically rejected UPDATEs gets them
  silently dropped (log Warning + `UpdatesRejected` metric) instead of a session reset; operators
  comparing against RFC-strict speakers will see BGPLite retain sessions others would close.
- **Tracker:** #94, #222, #284, #288, #322 (closed); recorded via #344.
