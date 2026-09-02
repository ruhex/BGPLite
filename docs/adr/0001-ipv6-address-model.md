# ADR 0001: IPv6 dual-stack address model

Status: accepted · Date: 2026-09-01 · Epic: #14 · Foundational issue: #15 · PR: (this)

## Context

The entire address model was 32-bit `uint`: `IpPrefix(uint Address, byte Length)`, `Route.Prefix`,
`RouteTable` key `(uint, byte)`, `PrefixCodec` 32-bit shifts. IPv6/MP-BGP (#14 phases 2+) cannot be
built on a 32-bit model, so the model had to change first. The model is referenced by ~13 source
files and the whole test suite, so the representation choice is expensive to reverse.

## Decision

1. **Representation**: `IpPrefix` becomes a family-aware value type over `UInt128`:
   - IPv4 → the address occupies the **low 32 bits**, `IsIpv4 = true` (no IPv4-mapped `::ffff:`
     prefix in the stored value — the flag carries the family, the numeric value stays small).
   - IPv6 → the full 128 bits, `IsIpv4 = false`.
   - `bool IsIpv4` is an explicit stored field (not derived from the address): an IPv4 /8 mask
     would erase the `::ffff:` marker if the family were derived from an IPv4-mapped layout.
2. **Canonicalization invariant**: the constructor masks host bits to the network address. An
   `IpPrefix` value is always a valid network key — this permanently closes the #7
   "mask-at-insert" class of defects at the type level.
3. **Dual-stack keys**: `Route.Key` / the `RouteTable` dictionary key carry `IsIpv4` alongside
   `(Address, Length)`. The IPv4 low-bits form cannot collide with a full-128 IPv6 form that has
   `IsIpv4 = false`; the flag in the key makes that explicit and total.
4. **Codec**: `PrefixCodec.Encode` is family-aware (IPv4 → length byte ≤ 32 + ≤ 4 data bytes;
   IPv6 → length byte ≤ 128 + ≤ 16 data bytes, big-endian). `Decode` remains the IPv4-NLRI parser
   (rejects length > 32 per RFC 4271 §4.3 — the IPv4 UPDATE NLRI field is IPv4-only);
   `Decode6` is the IPv6-NLRI parser for the MP_REACH/MP_UNREACH phase.
5. **128-bit converters**: `BgpConstants.ToUInt128(IPAddress)` / `FromUInt128(value, isIpv4)` /
   `ToUint32OrThrow(value, field)` — the wrong-family guard throws instead of silently truncating
   (model-level fix for #13). The existing `IPAddressToUint`/`UintToIPAddress` remain for the
   IPv4-only router-id / next-hop paths.
6. **Aggregation**: `ExactUnionPrefixAggregator` groups by (communities, IsIpv4) — IPv4 and IPv6
   prefixes are never merged into one summary even with identical tags. Interval math is
   generalized to UInt128. (Delivered as planned in #14 phase 3, together with the
   longest-prefix-match lookup on `RouteTable`.)

## Consequences

- `uint` → `UInt128` widening is implicit, so IPv4 construction sites (`Route { Prefix = 0xC0A80000u }`)
  keep compiling unchanged; narrowing reads cast explicitly.
- Phase 2 delivered IPv6 NLRI via MP_REACH/MP_UNREACH; until then the aggregator's `length > 32`
  skip defensively dropped inbound IPv6 routes. #14 phase 3 closed the interim gap: the aggregator
  is now fully 128-bit and family-partitioned, and `RouteTable` gained a longest-prefix-match
  lookup.
- `Route.NextHop` stayed IPv4 `uint` until Phase 2 delivered the MP_REACH next-hop (RFC 2545).
- Each later phase builds on this model without re-breaking it; the epic's phased checklist
  (#14 phases 2–5) tracks the remaining work.

## Alternatives considered

- **`IPAddress`-based `IpPrefix`**: reference type — allocation per prefix on the routing hot
  path and no cheap masking/equality. Rejected.
- **IPv4-mapped storage** (`::ffff:0:0/96` prefix inside the value): canonical dual-stack form,
  but family cannot be derived after masking (an IPv4 /8 mask erases the `::ffff:` marker), so a
  stored family flag is required anyway — and the mapped marker would corrupt under generic 128-bit
  masking. Rejected in favor of the low-bits form + flag.
- **`byte[16]`**: array identity breaks record equality; `ReadOnlyMemory<byte>` allocates.
  Rejected.
