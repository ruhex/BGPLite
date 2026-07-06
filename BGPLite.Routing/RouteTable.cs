using System.Collections.Concurrent;

namespace BGPLite.Routing;

public sealed class RouteTable
{
    private readonly ConcurrentDictionary<(uint Prefix, byte Length), Route> _routes = new();

    public int Count => _routes.Count;

    public bool AddOrUpdate(Route route)
    {
        // #85: avoid ConcurrentDictionary.AddOrUpdate's closure allocations (two delegate lambdas
        // per call). The try-pattern is allocation-free and equivalent: TryAdd for the new-key
        // case, indexer for the update case.
        if (!_routes.TryAdd(route.Key, route))
        {
            _routes[route.Key] = route;
            return false;
        }
        return true;
    }

    public bool Remove(uint prefix, byte length) =>
        _routes.TryRemove((prefix, length), out _);

    public Route? Get(uint prefix, byte length) =>
        _routes.TryGetValue((prefix, length), out var route) ? route : null;

    public IReadOnlyList<Route> GetAll() =>
        _routes.Values.ToList();

    /// <summary>Enumerates current routes without materializing a snapshot list (one allocation fewer than GetAll).</summary>
    public IEnumerable<Route> Enumerate() => _routes.Values;

    public void Clear() =>
        _routes.Clear();
}
