using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<(uint Prefix, byte Length), Entry> _routes = new();

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
        // case, indexer for the update case.
        var entry = new Entry(route, owner);
        if (!_routes.TryAdd(route.Key, entry))
        {
            _routes[route.Key] = entry;
            return false;
        }
        Interlocked.Increment(ref _count);
        return true;
    }

    /// <summary>Unconditional removal, regardless of owner — administrative paths only.</summary>
    public bool Remove(uint prefix, byte length)
    {
        var removed = _routes.TryRemove((prefix, length), out _);
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
    public bool RemoveOwnedBy(uint prefix, byte length, object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var key = (prefix, length);
        if (!_routes.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Owner, owner))
            return false;

        var removed = ((ICollection<KeyValuePair<(uint Prefix, byte Length), Entry>>)_routes)
            .Remove(new KeyValuePair<(uint Prefix, byte Length), Entry>(key, entry));
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
        ArgumentNullException.ThrowIfNull(owner);

        var removed = 0;
        foreach (var pair in _routes)
        {
            if (!ReferenceEquals(pair.Value.Owner, owner))
                continue;
            if (((ICollection<KeyValuePair<(uint Prefix, byte Length), Entry>>)_routes).Remove(pair))
                removed++;
        }
        if (removed > 0)
            Interlocked.Add(ref _count, -removed);
        return removed;
    }

    public Route? Get(uint prefix, byte length) =>
        _routes.TryGetValue((prefix, length), out var entry) ? entry.Route : null;

    public IReadOnlyList<Route> GetAll()
    {
        var routes = new List<Route>(Count);
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
