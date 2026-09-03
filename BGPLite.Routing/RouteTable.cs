using System.Collections.Concurrent;
using BGPLite.Protocol;

namespace BGPLite.Routing;

/// <summary>
/// The shared route table. Each entry records which session installed it, so a withdrawal can be a
/// compare-and-remove against the current owner rather than a delete by prefix (#289).
/// <para>
/// This is a minimal stand-in for the per-peer Adj-RIBs-In of RFC 4271 §3.2, not a replacement for
/// them: there is still one table and one entry per prefix, so a later announcement for a prefix
/// replaces the earlier one and its owner with it. What the ownership tag buys is that a peer can
/// only remove an entry it currently owns — it cannot delete the startup seed, another peer's
/// route, or a route of its own that has since been replaced by someone else.
/// </para>
/// </summary>
public sealed class RouteTable
{
    private readonly ConcurrentDictionary<(UInt128 Prefix, byte Length, bool IsIpv4), Entry> _routes = new();

    // #343: maintained count — every successful mutation adjusts it exactly once (1:1 with the
    // dictionary transition), so Count is an O(1) Volatile.Read instead of ConcurrentDictionary.Count,
    // which acquires ALL lock strips. That read ran twice per inbound UPDATE on the session read
    // loops, racing every writer. Transiently approximate only within a concurrent-mutation window
    // (adjust happens after the dictionary op); exact whenever no mutation is in flight.
    private int _count;

    /// <summary>
    /// A stored route plus the object that installed it. A <c>record struct</c> so the generated
    /// structural equality gives <see cref="RemoveOwnedBy"/> its compare-and-remove: both the
    /// <see cref="Route"/> instance and the <see cref="Owner"/> reference must still match.
    /// </summary>
    private readonly record struct Entry(Route Route, object? Owner);

    /// <summary>Current number of routes — O(1) via the maintained counter (#343). Transiently
    /// approximate under concurrent mutation; exact when quiescent.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Installs a route with no owner — startup seeding and tests. Nobody can remove it via <see cref="RemoveOwnedBy"/>.</summary>
    public bool AddOrUpdate(Route route) => AddOrUpdate(route, owner: null);

    /// <summary>
    /// Installs a route owned by <paramref name="owner"/>, replacing whatever is at that prefix.
    /// Returns true when the prefix was new. Replacing transfers ownership: the previous owner can
    /// no longer remove it, which is the point — its route is gone either way.
    /// </summary>
    public bool AddOrUpdate(Route route, object? owner)
    {
        // #85: avoid ConcurrentDictionary.AddOrUpdate's closure allocations (two delegate lambdas
        // per call). The try-pattern is allocation-free and equivalent: TryAdd for the new-key
        // case, TryUpdate for the replace case.
        // #346 (CodeRabbit review): a plain indexer for the replace case is NOT count-safe —
        // TryAdd returning false does not guarantee the key still exists by the write; a
        // concurrent remove in that window let the indexer re-insert the entry without
        // incrementing _count, drifting it permanently (negative after enough removals). The CAS
        // pair keeps the 1:1 transition invariant: TryAdd wins the new-key transition (+1);
        // TryGetValue+TryUpdate wins a true replace (±0) — TryUpdate succeeds only against the
        // value just read, so a key that vanished (or was replaced) in between loops back to
        // TryAdd, which then increments. Entry's record-struct value equality is fine here:
        // TryUpdate success merely proves the key was present, which is all ±0 needs.
        var entry = new Entry(route, owner);
        while (true)
        {
            if (_routes.TryAdd(route.Key, entry))
            {
                Interlocked.Increment(ref _count);
                return true;
            }
            if (_routes.TryGetValue(route.Key, out var existing) &&
                _routes.TryUpdate(route.Key, entry, existing))
            {
                // #377 review: ownership transferred — the PREVIOUS owner's session bookkeeping
                // (e.g. the #304 per-peer prefix set) must learn it no longer holds this key,
                // or its count drifts upward on overlaps and can trip the cap it no longer owns.
                // Raised AFTER the swap wins; a stale previous-owner read only means a late
                // notification, which the remove-if-present handlers tolerate.
                if (!ReferenceEquals(existing.Owner, owner) && existing.Owner is not null)
                    EntryOwnershipLost?.Invoke(existing.Owner, route.Key);
                return false; // replaced an entry that was still present — no count transition
            }
        }
    }

    /// <summary>
    /// Fired (on the replacing caller's thread) after an <see cref="AddOrUpdate"/> swap takes a
    /// key away from a previous owner (#377 review). Subscribers must tolerate invocation from
    /// any thread and duplicate/late notifications — treat it as "you MIGHT have lost this key".
    /// </summary>
    public event Action<object, (UInt128 Prefix, byte Length, bool IsIpv4)>? EntryOwnershipLost;

    /// <summary>Unconditional removal, regardless of owner — administrative paths only (IPv4).</summary>
    public bool Remove(uint prefix, byte length) => Remove((UInt128)prefix, length, isIpv4: true);

    /// <summary>Unconditional removal, regardless of owner — administrative paths only.</summary>
    public bool Remove(UInt128 prefix, byte length, bool isIpv4 = true)
    {
        var removed = _routes.TryRemove((prefix, length, isIpv4), out _);
        if (removed)
            Interlocked.Decrement(ref _count);
        return removed;
    }

    /// <summary>
    /// Removes the route at <paramref name="prefix"/>/<paramref name="length"/> only if
    /// <paramref name="owner"/> still owns it, and returns whether it did. This is the withdrawal
    /// path: RFC 4271 §9 removes the route received from that peer, so a peer must not be able to
    /// delete an entry it never installed — or one it installed but another peer has since replaced.
    /// <para>
    /// The removal is a single atomic compare-and-remove via the explicit
    /// <see cref="ICollection{T}"/> implementation, which removes the pair only when key AND value
    /// match — the same idiom <c>BgpServer.RemoveSessionIfOwner</c> uses. A plain
    /// read-then-<c>TryRemove</c> would race with a concurrent replacement and delete the new owner's
    /// route.
    /// </para>
    /// </summary>
    public bool RemoveOwnedBy(uint prefix, byte length, object owner) =>
        RemoveOwnedBy((UInt128)prefix, length, isIpv4: true, owner);

    public bool RemoveOwnedBy(UInt128 prefix, byte length, bool isIpv4 = true, object owner = null!)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var key = (prefix, length, isIpv4);
        if (!_routes.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Owner, owner))
            return false;

        var removed = ((ICollection<KeyValuePair<(UInt128 Prefix, byte Length, bool IsIpv4), Entry>>)_routes)
            .Remove(new KeyValuePair<(UInt128 Prefix, byte Length, bool IsIpv4), Entry>(key, entry));
        if (removed)
            Interlocked.Decrement(ref _count);
        return removed;
    }

    /// <summary>
    /// Removes every entry still owned by <paramref name="owner"/> and returns how many went — the
    /// session-close counterpart to <see cref="RemoveOwnedBy"/>. RFC 4271 §8.2.2 has a speaker
    /// "delete all routes associated with this connection" on every transition out of Established,
    /// and without it a peer's announcements outlived its session forever: nothing else in the
    /// server removes an entry, so a peer could reconnect and add another batch indefinitely (#313).
    /// <para>
    /// Each removal is the same atomic compare-and-remove <see cref="RemoveOwnedBy"/> performs, so
    /// an entry another peer has taken over between the scan and the delete stays put — the losing
    /// session must not delete the winner's route, exactly as in #289. Enumerating a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> while removing from it is safe and defined:
    /// the enumerator is a moment-in-time view, not a snapshot, and never throws for concurrent
    /// modification.
    /// </para>
    /// </summary>
    public int RemoveAllOwnedBy(object owner)
    {
        return RemoveAllOwnedBy(owner, isIpv4: true) + RemoveAllOwnedBy(owner, isIpv4: false);
    }

    /// <summary>
    /// #467: family-scoped variant — removes every entry still owned by <paramref name="owner"/>
    /// in ONE address family and returns how many went (the RFC 7606 §3(j) "AFI/SAFI disable"
    /// withdrawal). Each removal is the same atomic compare-and-remove as the full scan, so an
    /// entry another peer has taken over between the scan and the delete stays put.
    /// </summary>
    public int RemoveAllOwnedBy(object owner, bool isIpv4)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var removed = 0;
        foreach (var pair in _routes)
        {
            if (pair.Key.IsIpv4 != isIpv4)
                continue;
            if (!ReferenceEquals(pair.Value.Owner, owner))
                continue;
            if (((ICollection<KeyValuePair<(UInt128 Prefix, byte Length, bool IsIpv4), Entry>>)_routes).Remove(pair))
                removed++;
        }
        if (removed > 0)
            Interlocked.Add(ref _count, -removed);
        return removed;
    }

    /// <summary>Removes every entry owned by <paramref name="owner"/> (session teardown) —
    /// counts the removals for the maintained counter.</summary>
    /// <inheritdoc/>


    public Route? Get(UInt128 prefix, byte length, bool isIpv4 = true) =>
        _routes.TryGetValue((prefix, length, isIpv4), out var entry) ? entry.Route : null;

    /// <summary>
    /// Longest-prefix-match lookup (#14 phase 3): the stored route whose network most
    /// specifically contains <paramref name="address"/>, or null. Probes candidate prefix
    /// lengths from the family maximum down to /0 — at most 33 (IPv4) / 129 (IPv6) dictionary
    /// lookups — and is family-scoped: an IPv6 address never matches an IPv4 entry and vice
    /// versa (the family is part of the key). Read-only: it never installs or replaces anything,
    /// unlike <see cref="AddOrUpdate"/>. There is no per-packet consumer today (a route server
    /// does not forward); this is the lookup the epic's routing layer requires for /0..128
    /// coverage checks and policy decisions.
    /// </summary>
    public Route? GetLongestPrefixMatch(UInt128 address, bool isIpv4 = true)
    {
        for (var length = isIpv4 ? 32 : 128; length >= 0; length--)
        {
            var network = address & IpPrefix.Mask((byte)length, isIpv4);
            if (_routes.TryGetValue((network, (byte)length, isIpv4), out var entry))
                return entry.Route;
        }
        return null;
    }

    public IReadOnlyList<Route> GetAll()
    {
        // #346: clamp the capacity hint — a hint must never turn a hypothetical negative count
        // into List<Route>(negative) throwing ArgumentOutOfRangeException on the API read path.
        var routes = new List<Route>(Math.Max(0, Count));
        foreach (var entry in _routes.Values)
            routes.Add(entry.Route);
        return routes;
    }

    /// <summary>Enumerates current routes without materializing a snapshot list (one allocation fewer than GetAll).</summary>
    public IEnumerable<Route> Enumerate()
    {
        foreach (var entry in _routes.Values)
            yield return entry.Route;
    }

    /// <summary>
    /// Enumerates only the routes installed with no owner — in practice the startup seed written by
    /// <c>RouteSeedingService</c>. Excludes everything a peer announced inbound, which
    /// <c>BgpSession.HandleUpdateAsync</c> installs owned by its session.
    /// <para>
    /// This is the advertise-side counterpart to <see cref="RemoveOwnedBy"/>: the owner tag exists so
    /// a peer can neither remove what it does not own nor have what it injected handed to somebody
    /// else. Used by <c>RouteAssembler</c>'s shared-table fallback, which would otherwise re-advertise
    /// one peer's inbound announcements to every other peer (#307).
    /// </para>
    /// </summary>
    public IEnumerable<Route> EnumerateUnowned()
    {
        foreach (var entry in _routes.Values)
        {
            if (entry.Owner is null)
                yield return entry.Route;
        }
    }

    public void Clear()
    {
        // TryRemove loop rather than _routes.Clear(): each successful removal decrements the
        // maintained counter, keeping the 1:1 mutation↔transition invariant even when a writer
        // adds concurrently with the clear (#343).
        foreach (var key in _routes.Keys)
            if (_routes.TryRemove(key, out _))
                Interlocked.Decrement(ref _count);
    }
}
