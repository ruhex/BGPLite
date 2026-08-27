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

    /// <summary>
    /// A stored route plus the object that installed it. A <c>record struct</c> so the generated
    /// structural equality gives <see cref="RemoveOwnedBy"/> its compare-and-remove: both the
    /// <see cref="Route"/> instance and the <see cref="Owner"/> reference must still match.
    /// </summary>
    private readonly record struct Entry(Route Route, object? Owner);

    public int Count => _routes.Count;

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
        return true;
    }

    /// <summary>Unconditional removal, regardless of owner — administrative paths only.</summary>
    public bool Remove(uint prefix, byte length) =>
        _routes.TryRemove((prefix, length), out _);

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

        return ((ICollection<KeyValuePair<(uint Prefix, byte Length), Entry>>)_routes)
            .Remove(new KeyValuePair<(uint Prefix, byte Length), Entry>(key, entry));
    }

    public Route? Get(uint prefix, byte length) =>
        _routes.TryGetValue((prefix, length), out var entry) ? entry.Route : null;

    public IReadOnlyList<Route> GetAll()
    {
        var routes = new List<Route>(_routes.Count);
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

    public void Clear() =>
        _routes.Clear();
}
