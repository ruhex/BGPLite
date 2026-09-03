using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using BGPLite.Configuration;

namespace BGPLite.Api;

/// <summary>
/// Per-client-IP token-bucket rate limiter with IDLE-PARTITION EVICTION (#423).
/// <para>
/// System.Threading.RateLimiting's <c>PartitionedRateLimiter</c> has no idle eviction: every
/// client IP ever seen held its bucket (and its AutoReplenishment timer) for the process lifetime —
/// unbounded growth on a long-lived deployment, and with <c>Api.TrustXRealIp</c> the partition key
/// is client-controlled (rotating <c>X-Real-IP</c> minted fresh buckets per request, both defeating
/// the limit and growing the partition table). This registry keeps one
/// <see cref="TokenBucketRateLimiter"/> per IP, stamps last-access on every acquire, and —
/// amortized, every <see cref="SweepEvery"/> acquires — removes and disposes partitions idle for
/// longer than the threshold (default: 20 replenishment periods, floor 5 minutes).
/// </para>
/// <para>
/// A request racing a sweep's dispose gets a fresh bucket (the acquire retries once) — a rare
/// request loses its refill state, never correctness. 429/deny semantics, queue-free, are
/// identical to the <c>PartitionedRateLimiter</c> this replaces (#116).
/// </para>
/// </summary>
internal sealed class ClientIpRateLimiter : IDisposable, IAsyncDisposable
{
    internal const int DefaultSweepEvery = 64;
    private static readonly TimeSpan MinIdleThreshold = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (TokenBucketRateLimiter Limiter, long LastAccessTicks)> _partitions = new();
    private readonly TokenBucketRateLimiterOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleThreshold;
    private readonly int _sweepEvery;
    private int _acquiresSinceSweep;

    public ClientIpRateLimiter(
        ApiRateLimitConfig cfg,
        TimeProvider? timeProvider = null,
        TimeSpan? idleThreshold = null,
        int? sweepEvery = null)
    {
        _options = new TokenBucketRateLimiterOptions
        {
            TokenLimit = Math.Max(1, cfg.TokenLimit),
            TokensPerPeriod = Math.Max(1, cfg.TokensPerPeriod),
            ReplenishmentPeriod = TimeSpan.FromSeconds(Math.Max(1, cfg.PeriodSeconds)),
            QueueLimit = 0,         // deny immediately (429) when no tokens — never queue
            AutoReplenishment = true
        };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleThreshold = idleThreshold ?? TimeSpan.FromTicks(Math.Max(MinIdleThreshold.Ticks, 20 * _options.ReplenishmentPeriod.Ticks));
        _sweepEvery = sweepEvery ?? DefaultSweepEvery;
    }

    /// <summary>Partitions currently tracked (test/observability — eviction coverage).</summary>
    internal int TrackedCount => _partitions.Count;

    public ValueTask<RateLimitLease> AcquireAsync(string clientIp)
    {
        // #423: amortized idle-partition eviction — see the class doc.
        if (Interlocked.Increment(ref _acquiresSinceSweep) >= _sweepEvery)
        {
            Volatile.Write(ref _acquiresSinceSweep, 0);
            SweepIdle();
        }

        var limiter = GetOrAdd(clientIp);
        RateLimitLease lease;
        try
        {
            lease = limiter.AttemptAcquire();
        }
        catch (ObjectDisposedException)
        {
            // Lost a race with the eviction sweep's Dispose: mint a fresh bucket and retry once.
            // Narrow and benign — the request sees an unfilled bucket instead of failing.
            limiter = new TokenBucketRateLimiter(_options);
            _partitions[clientIp] = (limiter, Stamp());
            lease = limiter.AttemptAcquire();
        }

        return ValueTask.FromResult(lease);
    }

    private TokenBucketRateLimiter GetOrAdd(string clientIp)
    {
        var now = Stamp();
        while (true)
        {
            if (_partitions.TryGetValue(clientIp, out var existing))
            {
                // Touch last-access; the limiter reference is unchanged, so the tuple write is
                // safe even against a concurrent sweep (compare-remove either wins before the
                // touch — the request retries on a fresh bucket — or loses and the touch keeps
                // the partition alive).
                _partitions[clientIp] = (existing.Limiter, now);
                return existing.Limiter;
            }

            var created = new TokenBucketRateLimiter(_options);
            if (_partitions.TryAdd(clientIp, (created, now)))
                return created;
            created.Dispose(); // lost the first-insert race — dispose the spare (no timer leak)
        }
    }

    private void SweepIdle()
    {
        var now = Stamp();
        foreach (var (key, entry) in _partitions)
        {
            if (now - entry.LastAccessTicks < _idleThreshold.Ticks) continue;
            // Compare-remove: an entry touched since the snapshot is kept (the pair no longer
            // matches), so an active partition is never disposed mid-request.
            if (_partitions.TryRemove(new KeyValuePair<string, (TokenBucketRateLimiter, long)>(key, entry)))
                entry.Limiter.Dispose();
        }
    }

    private long Stamp() => _timeProvider.GetUtcNow().UtcDateTime.Ticks;

    public void Dispose()
    {
        foreach (var (_, entry) in _partitions)
            entry.Limiter.Dispose();
        _partitions.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
