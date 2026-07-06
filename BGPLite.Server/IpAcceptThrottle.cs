using System.Collections.Concurrent;

namespace BGPLite.Server;

/// <summary>
/// Per-source-IP accept throttle for the BGP listener (#115): bounds how many inbound TCP connects
/// a single remote IP may open within a rolling 60s window. Defends one-IP accept floods without
/// capping the count of legitimate established sessions — a route server is designed to hold many
/// peers (1 → 9999+), which is capacity/business logic, not a security control. The flood vector
/// here is connections from a single source, not the established-session count.
/// <para>
/// Thread-safe sliding-window counter: each distinct IP gets a bounded list of recent accept
/// timestamps guarded by a per-IP lock; entries older than the window are pruned on every access.
/// Stale entries (an IP idle longer than one window, with no recent timestamp) are evicted from the
/// tracker on an amortized sweep so a distinct-IP flood cannot grow the tracker without bound. The
/// list per IP is bounded by <c>limit+1</c>. The OS firewall (nftables) is the PRIMARY gate; this
/// is a cheap in-app backstop.
/// </para>
/// </summary>
internal sealed class IpAcceptThrottle
{
    private const int SweepEvery = 64;
    private readonly int _maxPerMinute;
    private readonly long _windowTicks;
    private readonly ConcurrentDictionary<string, Window> _byIp = new(StringComparer.Ordinal);
    private readonly Func<long> _nowTicks;
    private int _callsSinceSweep;
    // Coarse throttle-level lock (#133): serializes the dictionary-mutation parts of TryAccept and
    // SweepStale so a sweep cannot TryRemove a Window that a concurrent TryAccept just refreshed.
    // The prior per-Window lock checked staleness under the Window lock but called _byIp.TryRemove
    // OUTSIDE any dictionary-level atomicity — a racing TryAccept could GetOrAdd the same Window,
    // record a fresh accept, and then have that entry removed by the sweep, orphaning the
    // just-recorded accept and effectively resetting the IP's limit. The accept path tolerates
    // serialization (it is the throttle itself — not a hot path).
    private readonly object _dictLock = new();

    public IpAcceptThrottle(int maxPerMinute, Func<long>? nowTicks = null)
    {
        _maxPerMinute = maxPerMinute;
        _windowTicks = TimeSpan.FromMinutes(1).Ticks;
        _nowTicks = nowTicks ?? (() => DateTime.UtcNow.Ticks);
    }

    /// <summary>Number of distinct IPs currently tracked (test/observability).</summary>
    internal int TrackedCount => _byIp.Count;

    /// <summary>
    /// Pure sliding-window decision: prune timestamps older than the window, then allow iff the
    /// remaining count is below <paramref name="limit"/>. When allowed, the new <paramref name="nowTicks"/>
    /// timestamp is appended. Extracted as a pure function so the windowing math is unit-testable
    /// without threads, timers, or a clock. Returns the pruned (and possibly appended) timestamp
    /// list so the caller can store it back atomically.
    /// </summary>
    /// <param name="timestamps">This IP's prior accept timestamps (any order; not mutated).</param>
    /// <param name="nowTicks">Current UTC ticks (passed in, not read from a clock, for determinism).</param>
    /// <param name="windowTicks">Window length in ticks (e.g. one minute).</param>
    /// <param name="limit">Maximum accepts allowed within the window. &lt;= 0 always allows.</param>
    /// <param name="updated">The timestamp list to store back: pruned entries, plus <paramref name="nowTicks"/>
    /// only when allowed. When denied, holds only the pruned entries (the rejected accept is NOT
    /// recorded, so it does not extend the window).</param>
    /// <returns>True when this accept is within the limit; false when it must be rejected.</returns>
    internal static bool Decide(
        IReadOnlyList<long> timestamps, long nowTicks, long windowTicks, int limit,
        out List<long> updated)
    {
        var cutoff = nowTicks - windowTicks;
        updated = new List<long>(timestamps.Count + 1);
        foreach (var t in timestamps)
            if (t > cutoff)
                updated.Add(t);

        if (limit <= 0 || updated.Count < limit)
        {
            updated.Add(nowTicks);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Records an accept from <paramref name="ip"/> and returns whether it is within the limit.
    /// False = over the limit for the current minute window — the caller must close the just-accepted
    /// socket immediately WITHOUT spawning a session. When <c>maxPerMinute &lt;= 0</c> (disabled) this
    /// always returns true and touches no state.
    /// </summary>
    public bool TryAccept(string ip)
    {
        if (_maxPerMinute <= 0) return true;

        var nowTicks = _nowTicks();
        bool allowed;
        // Hold _dictLock across GetOrAdd + Decide + store so SweepStale cannot remove this Window
        // between the refresh and the store (#133). The Decide computation is cheap (small list),
        // so the critical section is short.
        lock (_dictLock)
        {
            var window = _byIp.GetOrAdd(ip, _ => new Window());
            allowed = Decide(window.Timestamps, nowTicks, _windowTicks, _maxPerMinute, out var updated);
            window.Timestamps = updated;
        }

        // Amortized eviction of idle IPs (no timestamp newer than one window) so the tracker can't
        // grow without bound under a distinct-IP flood. Active/recent IPs are always retained.
        if (Interlocked.Increment(ref _callsSinceSweep) >= SweepEvery)
        {
            _callsSinceSweep = 0;
            SweepStale(nowTicks);
        }
        return allowed;
    }

    /// <summary>Removes tracked IPs whose every timestamp is older than one window (idle IPs).</summary>
    internal void SweepStale(long nowTicks)
    {
        var cutoff = nowTicks - _windowTicks;
        // Hold _dictLock across the whole sweep so TryAccept cannot refresh a Window we are about to
        // remove. The snapshot enumeration is safe under lock (no concurrent writers); removing while
        // iterating ConcurrentDictionary is supported, but the lock makes the staleness-check + remove
        // atomic against a racing TryAccept (#133).
        lock (_dictLock)
        {
            foreach (var (ip, window) in _byIp)
            {
                var stale = window.Timestamps.Count == 0 || IsAllStale(window.Timestamps, cutoff);
                if (stale)
                    _byIp.TryRemove(ip, out _);
            }
        }
    }

    private static bool IsAllStale(IReadOnlyList<long> timestamps, long cutoff)
    {
        for (var i = 0; i < timestamps.Count; i++)
            if (timestamps[i] > cutoff) return false;
        return true;
    }

    /// <summary>Per-IP mutable window state, guarded by locking the instance itself.</summary>
    private sealed class Window
    {
        // List<> (not Queue<>): Decide prunes arbitrary old entries from the middle of the window,
        // and a List indexed walk + append is cheaper than a Queue dequeue loop for small N.
        public List<long> Timestamps = new();
    }
}
