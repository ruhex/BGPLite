using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BGPLite.Contracts;

namespace BGPLite;

/// <summary>
/// Background timer that periodically checks all prefix sources for changes (#214). Uses conditional
/// requests (ETag / Last-Modified → 304 Not Modified) so unchanged sources cost ~1 KB per check.
/// Only sources whose data actually changed trigger peer route refreshes — no unnecessary BGP churn.
/// <para>
/// Per-source poll interval: sources supporting conditional requests (HTTP/file) are polled at
/// <see cref="AutoRefreshConfig.IntervalSeconds"/> (304s are cheap); sources without ETag support
/// (RIPEstat ASN) at the longer <see cref="AutoRefreshConfig.NoEtagIntervalSeconds"/> to avoid
/// hammering the upstream. A single timer ticks at <c>min(IntervalSeconds, NoEtagIntervalSeconds)</c>;
/// each tick re-polls only sources whose per-source next-check time has elapsed.
/// </para>
/// <para>
/// Jitter: between source checks within a tick, a random delay up to
/// <see cref="AutoRefreshConfig.MaxJitterMs"/> spreads conditional requests to avoid a burst to the
/// same host (GitHub rate limits, RIPEstat 429s).
/// </para>
/// <para>
/// Disabled by default. Enable via <c>AutoRefresh: Enabled: true</c> in config.
/// </para>
/// </summary>
internal sealed class PrefixAutoRefreshService : IHostedService, IDisposable
{
    private readonly IPrefixSourceService _prefixSources;
    private readonly ISessionManager _sessionManager;
    private readonly AutoRefreshConfig _config;
    private readonly ILogger<PrefixAutoRefreshService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Random _jitterRandom;
    private readonly CancellationTokenSource _cts = new();
    // Source names are fixed at process start (config is not hot-reloaded for PrefixSources). Captured
    // once from AppConfig — avoids sync-over-async enumeration through the service at tick time.
    private readonly List<string> _sourceNames;
    // Per-source next-check instant (UTC). A source is re-polled when the timer ticks past this.
    private readonly Dictionary<string, DateTimeOffset> _nextCheck = new();
    private PeriodicTimer? _timer;
    private Task? _loopTask;

    public PrefixAutoRefreshService(
        IPrefixSourceService prefixSources,
        ISessionManager sessionManager,
        AppConfig appConfig,
        ILogger<PrefixAutoRefreshService> logger,
        TimeProvider? timeProvider = null,
        Random? jitterRandom = null)
    {
        _prefixSources = prefixSources;
        _sessionManager = sessionManager;
        _config = appConfig.AutoRefresh ?? new AutoRefreshConfig();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterRandom = jitterRandom ?? new Random();
        _sourceNames = [.. (appConfig.PrefixSources ?? []).Select(s => s.Name)]; // #477: YAML null = no sources
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Auto-refresh: disabled (AutoRefresh.Enabled = false)");
            return Task.CompletedTask;
        }

        // Guard: Enabled must be true AND IntervalSeconds must be > 0 (CodeRabbit #215).
        if (_config.IntervalSeconds <= 0)
        {
            _logger.LogWarning("Auto-refresh: Enabled=true but IntervalSeconds={Sec} — disabled", _config.IntervalSeconds);
            return Task.CompletedTask;
        }

        var intervalSeconds = Math.Max(60, _config.IntervalSeconds);
        var noEtagSeconds = _config.NoEtagIntervalSeconds > 0
            ? Math.Max(intervalSeconds, _config.NoEtagIntervalSeconds)
            : intervalSeconds;
        // Tick at the shorter interval so conditional sources are polled on schedule; non-conditional
        // sources are gated by their own per-source nextCheck (noEtagSeconds) and skipped on most ticks.
        var tickSeconds = Math.Min(intervalSeconds, noEtagSeconds);
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(tickSeconds), _timeProvider);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), cancellationToken);
        _logger.LogInformation(
            "Auto-refresh: enabled, tick every {Tick}s (etag interval {Etag}s, no-etag interval {NoEtag}s)",
            tickSeconds, intervalSeconds, noEtagSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        // Wait for the loop BEFORE disposing the timer: the loop may be parked in
        // WaitForNextTickAsync on this very timer, and disposing it first faults that wait with
        // ObjectDisposedException — the generic "loop faulted" log noise on every affected
        // shutdown (#321 item 7). Cancellation above unwinds the wait; a stopped loop leaves no
        // waiter behind.
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-refresh loop faulted on shutdown"); }
            // The host token may have fired while the loop was still parked — give it a brief
            // token-less chance to observe _cts and exit before the timer goes away, or disposing
            // the timer faults the parked wait with an unobserved ObjectDisposedException (#321
            // review). It only fails to exit if cancellation itself is wedged.
            try { await _loopTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* loop faulted or still parked — best effort before disposal */ }
        }
        _timer?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                _logger.LogDebug("Auto-refresh: starting check cycle");
                var changed = await CheckSourcesAsync(ct);

                if (changed.Count > 0)
                {
                    _logger.LogInformation("Auto-refresh: {Count} sources changed — refreshing peers", changed.Count);
                    await _sessionManager.RefreshAllEstablishedAsync();
                }
                else
                {
                    _logger.LogDebug("Auto-refresh: no sources changed");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-refresh: cycle failed");
            }
        }
    }

    /// <summary>
    /// Polls every source whose next-check time has elapsed, applying jitter between checks. Returns
    /// the names of sources whose content actually changed. Updates each polled source's next-check
    /// time according to its conditional-request support.
    /// </summary>
    private async Task<HashSet<string>> CheckSourcesAsync(CancellationToken ct)
    {
        var changed = new HashSet<string>();
        var now = _timeProvider.GetUtcNow();
        var etagInterval = TimeSpan.FromSeconds(Math.Max(60, _config.IntervalSeconds));
        var noEtagInterval = _config.NoEtagIntervalSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(_config.IntervalSeconds, _config.NoEtagIntervalSeconds))
            : etagInterval;
        var maxJitter = _config.MaxJitterMs > 0
            ? TimeSpan.FromMilliseconds(Math.Min(_config.MaxJitterMs, 60_000))
            : TimeSpan.Zero;

        var polledInThisCycle = 0;
        foreach (var name in _sourceNames)
        {
            // First tick: _nextCheck has no entry yet — poll immediately to populate the cache.
            if (_nextCheck.TryGetValue(name, out var due) && now < due)
                continue; // not due yet (typical for no-ETag sources between their long intervals)

            // Jitter between source checks within a cycle: avoids a burst of conditional requests to
            // the same host (GitHub rate limits: 60 req/min unauthenticated). Applied before every
            // polled source EXCEPT the first in this cycle, so startup isn't artificially delayed and
            // sources already spaced by their per-source intervals don't stack an extra delay.
            if (maxJitter > TimeSpan.Zero && polledInThisCycle > 0)
            {
                // Cap the exclusive upper bound for Random.Next at int.MaxValue to avoid overflow on
                // the +1 (MaxJitterMs is already clamped to 60s above, so this is belt-and-braces).
                var upperExclusive = (int)Math.Min(maxJitter.TotalMilliseconds + 1, int.MaxValue);
                var jitter = TimeSpan.FromMilliseconds(_jitterRandom.Next(0, upperExclusive));
                if (jitter > TimeSpan.Zero)
                    await Task.Delay(jitter, _timeProvider, ct);
            }

            // Per-source isolation: a single failing source (RefreshAsync throws unexpectedly, or
            // SourceSupportsConditional faults on a bad factory lookup) must NOT abort the whole cycle
            // and skip the remaining due sources. RefreshAsync already swallows per-source load errors
            // internally (returns false); this guards the rest of the iteration.
            try
            {
                var isChanged = await _prefixSources.RefreshAsync(name, ct);
                if (isChanged)
                    changed.Add(name);
                polledInThisCycle++;

                // Schedule the next check based on whether the source supports conditional requests:
                // 304-capable sources are cheap → short interval; RIPEstat-style → long interval.
                var supportsConditional = _prefixSources.SourceSupportsConditional(name);
                var interval = supportsConditional ? etagInterval : noEtagInterval;
                _nextCheck[name] = _timeProvider.GetUtcNow() + interval;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-refresh: source '{Name}' check failed; skipping this cycle", name);
            }
        }
        return changed;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer?.Dispose();
    }
}
