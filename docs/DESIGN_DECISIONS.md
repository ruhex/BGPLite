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

### D6. Graceful Restart capability is not advertised
- **Decision:** the OPEN does not include the GR capability. The receiving-speaker half of RFC 4724
  (§4.2: retain and stale-mark a restarting peer's routes for its Restart Time, flush on expiry or
  End-of-RIB) is not implemented, and advertising the `<AFI=1, SAFI=1, F>` tuple promised behavior
  the code does not have. The sending-side conveniences gated on the `GracefulRestart` config are
  unchanged: an End-of-RIB marker after the initial route dump, and the GR-aware silent close on
  server shutdown (`StopAsync`). #14 phase 5 made the factory/parser per-family (IPv4/Unicast AND
  IPv6/Unicast tuples) and added the IPv6-family End-of-RIB (empty MP_UNREACH, RFC 4724 §2) for
  MP-IPv6-negotiated peers; the advertisement itself remains off until retention exists.
- **Context:** the capability was previously advertised by default while routes of a
  silently-disconnecting peer were flushed immediately (`RemoveAllOwnedBy`, #314) — a wire promise
  the code did not honor. RFC 4724 §4.2's MUST binds a speaker to the procedures it engages;
  stopping the advertisement is the honest half-measure until retention exists (#318 direction 1).
- **Consequence:** GR-capable peers no longer retain our routes across our restart — they flush on
  session end like any non-GR peer. The peer's GR advertisement is still parsed and logged.
  `RestartTime` / `GracefulRestartForwardingState` are accepted for config compatibility but unused
  while the capability is not advertised. Re-advertise only together with receiving-speaker
  retention.
- **Tracker:** #318 (open for direction 2 — implement the receiving half).

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
- **Context:** the sending-side GR conveniences (End-of-RIB, the GR-aware silent close in
  `StopAsync`) signal "this is a restart, retain" — but deletion is a permanent removal, not a
  restart, so the NOTIFICATION termination is the correct signal there regardless (and since D6 the
  capability is not advertised, so no peer retains our routes across ANY of our disconnects) (#323).
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
  unrecoverable — and discards only (D2). Scope: **UPDATE only**. A malformed OPEN received in
  Established is an FSM error (NOTIFICATION 5/0, #427) — RFC 4271 §8.2.2 makes ANY OPEN in
  Established an FSM input regardless of body validity, so there is nothing to "keep alive" for
  that message class.
- **MP carve-out (#467, recorded 2026-09-03):** RFC 7606 explicitly leaves MP_REACH_NLRI and
  MP_UNREACH_NLRI outside the keep-alive revision, so their failure classes follow their own
  policy instead of the paragraph above. A DUPLICATE MP attribute is answered with exactly one
  NOTIFICATION 3/1 (Malformed Attribute List) and a session reset — RFC 7606 §3(g) MUST. An MP
  flags conflict (RFC 4760 §4: both attributes are optional NON-transitive) is a session reset
  with 3/4 per the RFC 4271 §6.3 baseline (the Partial bit joins the Transitive bit in the
  conflict mask). An UNPARSEABLE MP VALUE is scoped to the offending AFI/SAFI tuple: a tuple
  this speaker does not support was never negotiated (RFC 4760 §8), so it only discards its
  UPDATE through the keep-alive path above — the session and the supported family stay
  untouched; a supported tuple whose value cannot be decoded takes the RFC 7606 §3(j)
  "AFI/SAFI disable" choice — every IPv6 route the session accepted from the peer is withdrawn,
  the family is ignored for the rest of the session, and the session itself stays up — the
  route-server rationale above, family-scoped; a value too short to even name its AFI/SAFI
  cannot be scoped to any family, so the §3(j) fallback is the session reset. Additionally, an
  MP_REACH next hop that is not a global IPv6 address (::, ::1, ff00::/8, fe80::/10 — RFC 2545
  §3) excludes that attribute's routes only: route-level exclusion, like the AS-loop rule, not
  a session error.
- **Consequence:** a peer sending structurally valid but semantically rejected UPDATEs gets them
  silently dropped (log Warning + `UpdatesRejected` metric) instead of a session reset; operators
  comparing against RFC-strict speakers will see BGPLite retain sessions others would close.
- **Tracker:** #94, #222, #284, #288, #322 (closed); recorded via #344.

### D19. Custom prefixes suppress the source prefixes they cover
- **Decision:** a custom prefix is the operator's explicit override: any route STRICTLY more specific
  than one is dropped from the peer's outbound list, regardless of its source or community set. An
  exact custom==source duplicate is NOT suppressed — `MergeDuplicatePrefixes` (#209) unions the
  communities, so the source's tags survive. Suppression runs in the assembler on the flat per-peer
  list, BEFORE the per-community-set aggregation — `ExactUnionPrefixAggregator` (D15, #82) itself is
  untouched and never merges across community sets.
- **Context:** operators add a broader custom prefix (e.g. a /16) to override a source list whose
  more-specifics (e.g. /24s in different communities) were still advertised alongside it — the two
  land in different aggregator groups and both went out (#220).
- **Consequence:** suppressed source prefixes lose their community tags on the wire — the receiving
  peer sees the custom-prefix community; that is the override's intent. Per-send log line reports the
  suppressed count. Idempotent across refreshes: every rebuild of the list applies the same
  deterministic filter, so `_advertisedPrefixes` stays consistent for withdrawals.
- **Tracker:** #220.

### D20. Community-less routes are denied under an active per-peer allowlist
- **Decision:** when a peer has an active outgoing community allowlist (`PeerCommunities` set), a
  route with NO COMMUNITY attribute is rejected; only tagged routes whose tag is in the allowlist
  pass. The no-allowlist fast path (everything passes) is unchanged.
- **Context:** the allowlist is the peer's consent to receive specific tags (#79); a community-less
  route carries no such consent, and the previous default-allow let untagged routes bypass an
  operator's filter entirely (PeerCommunityFilter.cs, #7 audit). Chosen over documenting
  default-allow after review (#389) — strictness matches the filter's purpose; in BGPLite every
  source path stamps communities via `ConfigCommunityResolver`, so community-less routes are
  exceptional (a failed/absent resolver override), not a legitimate class.
- **Consequence:** behavior change for deployments that used an allowlist AND relied on untagged
  routes flowing; such routes now stop at the filter (visible via the existing send logs).
- **Tracker:** #389.

### D22. IPv6 outbound advertisement is gated on negotiation AND a configured global next hop
- **Decision:** IPv6 routes are advertised to a peer only when BOTH hold: the peer negotiated
  MP IPv6/Unicast in OPEN, and `Bgp.NextHopIpv6` is configured (validated at startup as a global
  unicast address, 2000::/3). Otherwise IPv6 routes are suppressed for that send with a warning —
  never silently, and never encoded as classic IPv4 NLRI (which would corrupt the peer's table).
  v6 UPDATEs carry no classic NEXT_HOP (RFC 4760 §5 — the next hop rides in MP_REACH_NLRI,
  RFC 2545 §3 global form); IPv6 withdrawals ride MP_UNREACH_NLRI (RFC 4760 §7).
- **Context:** after the phase-2 codec (#15) and #407 fix the session could RECEIVE IPv6 routes
  but had no outbound path — sources were IPv4-only and the send path had no family split.
  Advertising IPv6 with the IPv4 router-id as next hop (or without the peer's negotiation) would
  be a route leak/blackhole, so the gate is deliberately double-sided and fail-visible.
- **Consequence:** IPv4-only deployments need no config change (no `NextHopIpv6` = no IPv6
  advertisements, one Warning per send that actually had v6 routes); MP-IPv6 deployments add one
  config line. The MP_REACH attribute is appended per batch (it embeds the batch's NLRI bytes) and
  goes last — attribute ordering is an implementation choice (D15).
- **Tracker:** #14 (phase 4b), #407.

### D21. IPv6 dual-stack address model (ADR 0001)
- **Decision:** `IpPrefix` is family-aware over `UInt128` — IPv4 in the low 32 bits with an
  explicit `IsIpv4` flag, IPv6 as the full 128 bits; the constructor masks host bits (canonical
  keys); `Route.Key`/`RouteTable` keys carry the family; the aggregator partitions by family.
  Full ADR: `docs/adr/0001-ipv6-address-model.md` (#15 phase 1). The family partition and
  128-bit interval math in `ExactUnionPrefixAggregator`, plus the `RouteTable`
  longest-prefix-match lookup, landed with #14 phase 3.
- **Tracker:** #15, #14.

## Management API

### D18. `X-Real-IP` is ignored by default, even behind trusted proxies
- **Decision:** a trusted proxy's `X-Real-IP` header is consulted only when the operator sets
  `Api.TrustXRealIp: true` (hot-reloadable). `X-Forwarded-For` handling is unchanged: walked
  right-to-left past trusted hops. Startup warns about the proxy requirements when TrustedProxies is
  configured; a runtime warning (once) fires when a trusted proxy yields no usable forwarding header.
- **Context:** unlike XFF, an X-Real-IP value cannot be verified against the trusted-hop chain — a
  proxy that passes the header through instead of overwriting it (plain nginx without
  `proxy_set_header X-Real-IP $remote_addr;`) turns it into an attacker-controlled input: fresh
  rate-limit buckets per request (#116 bypass) and a forged `/api/me` identity, which may surface
  token-bearing prefix-source URLs (#149). Direct (non-trusted) connections were already hardened by
  #117 — this closes the trusted-proxy hole (#256).
- **Consequence:** deployments that relied on an X-Real-IP-only proxy must set `TrustXRealIp: true`
  (intentional behavior change, secure by default); such a proxy is otherwise attributed to the
  proxy address, with the one-shot runtime warning pointing at the misconfiguration.
- **Tracker:** #256.

## Routing

### D23. Outbound routes are re-originated — AS_PATH carries only the local ASN
- **Decision:** BGPLite never prepends the received AS_PATH. Every advertised prefix
  (source-fed, custom, or RU-default) is re-originated: the outbound AS_PATH is built from the
  local ASN alone (`UpdateCodec.BuildUpdateAttributes`/`BuildAsPathAttributes`;
  `RouteAssembler.MakeRoute` passes `asPath: null`). MED, LOCAL_PREF, and received large
  communities are likewise not propagated; outbound communities are operator-stamped per source
  (one community per source/category, `ConfigCommunityResolver`).
- **Context:** BGPLite is an provisioning route server — it advertises operator-configured prefix
  sets, not transit routes. Peer-learned routes are never re-advertised at all (D13: the shared
  table exists for ownership-scoped withdrawals; `SharedTableRouteAssembler` re-advertises only
  the unowned startup seed). The inbound half of loop prevention IS implemented: a route whose
  AS_PATH contains the local ASN is excluded from installation (RFC 4271 §9.1.2).
- **Consequence (RFC 4271 §9.1.2):** AS-loop detection at RECEIVING speakers cannot recognize
  their own prefix coming back, because the returned path carries only BGPLite's ASN — an origin
  AS peering with BGPLite will not reject the route by loop detection and must rely on its own
  policy. Operators comparing against RFC-strict speakers should know the advertised paths are
  always exactly one ASN long.
- **Tracker:** #456 (documentation of long-standing behavior, flagged by the 2026-09-03 audit).

### D24. The MP IPv4/Unicast capability is never advertised
- **Decision:** the OPEN BGPLite sends never carries the Multiprotocol Extensions capability
  for AFI=1/SAFI=1, regardless of what the peer offers (`BgpSession.SendOpenAsync`).
  IPv4/Unicast is exchanged via the classic NLRI field only.
- **Context:** `SendOpenAsync` used to echo the peer's MP IPv4/Unicast offer back. RFC 5492 §3
  makes a capability usable on a peering only when both sides advertised it, and RFC 4760 §8
  requires the advertisement to mean the speaker supports that \<AFI, SAFI\> on receive — but
  the inbound path has no MP_REACH/MP_UNREACH AFI=1 handling at all (`MpReachCodec` accepts
  AFI=2 only). A conformant peer sending IPv4 NLRI via MP_REACH therefore had every such
  UPDATE discarded whole (RFC 7606 treat-as-withdraw) with the session looking healthy:
  negotiated route loss with no diagnostic beyond a Warning log line. Same
  advertise-without-implementing class as the Graceful Restart case (#318, D6) — the
  advertisement must not precede the implementation.
- **Consequence:** a peer that prefers MP carriage for IPv4 falls back to classic NLRI, which
  is the default family and works unchanged. IPv6/Unicast advertisement is untouched
  (mirrored when the peer offers it — the AFI=2 receiving half IS implemented). Reintroduce
  the advertisement only together with an AFI=1 decode path.
- **Tracker:** #466.
