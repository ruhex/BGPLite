using BGPLite.Configuration;
using BGPLite.Providers;
using BGPLite.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite;

/// <summary>
/// Background timer that periodically checks all prefix sources for changes (#214). Uses conditional
/// requests (ETag / Last-Modified → 304 Not Modified) so unchanged sources cost ~1 KB per check.
/// Only sources whose data actually changed trigger peer route refreshes — no unnecessary BGP churn.
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
    private readonly CancellationTokenSource _cts = new();
    private PeriodicTimer? _timer;
    private Task? _loopTask;

    public PrefixAutoRefreshService(
        IPrefixSourceService prefixSources,
        ISessionManager sessionManager,
        AppConfig appConfig,
        ILogger<PrefixAutoRefreshService> logger,
        TimeProvider? timeProvider = null)
    {
        _prefixSources = prefixSources;
        _sessionManager = sessionManager;
        _config = appConfig.AutoRefresh ?? new AutoRefreshConfig();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        var interval = TimeSpan.FromSeconds(Math.Max(60, _config.IntervalSeconds));
        _timer = new PeriodicTimer(interval, _timeProvider);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), cancellationToken);
        _logger.LogInformation("Auto-refresh: enabled, checking every {Interval} min",
            interval.TotalMinutes);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        _timer?.Dispose();
        if (_loopTask is not null)
        {
            try { await _loopTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logger.LogWarning(ex, "Auto-refresh loop faulted on shutdown"); }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && await _timer!.WaitForNextTickAsync(ct))
        {
            try
            {
                _logger.LogDebug("Auto-refresh: starting check cycle");
                var changed = await _prefixSources.RefreshAllAsync(ct);

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

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _timer?.Dispose();
    }
}
