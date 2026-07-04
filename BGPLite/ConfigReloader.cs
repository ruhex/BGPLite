using BGPLite.Api;
using BGPLite.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite;

/// <summary>
/// Hot-reloads the SOFT (non-session-disrupting) part of <c>appsettings.yml</c> while the BGP
/// service keeps running (#136). Watches the config file for changes; ~500 ms after the last change
/// (debounced — editors fire several events per save, and a partial write would be read mid-flight)
/// it reloads + validates the YAML and tells <see cref="ManagementApi.ApplyConfig"/> to swap the
/// reloadable derived state (TrustedProxies / CORS / rate &amp; concurrency limiters). Fields baked
/// into established sessions (Bgp.Asn/RouterId/HoldTime, Peers, ApiPort, PrefixSources, RipeStat,
/// communities) are NOT applied and require a restart — the reloader only updates the soft fields.
///
/// Resilience: a bad edit (malformed YAML, a config that fails Validate()) is caught and logged, and
/// the previous config stays in effect — the service is never crashed by a bad edit. This matches the
/// strict-YAML (#102) + Validate (#89) pipeline, re-run on every reload.
/// </summary>
public sealed class ConfigReloader : IHostedService, IDisposable
{
    private const int DebounceMs = 500;

    private readonly string _configPath;
    private readonly ManagementApi _managementApi;
    private readonly ILogger<ConfigReloader> _logger;
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private int _reloading; // 0 = idle, 1 = a reload is in progress (Interlocked)

    public ConfigReloader(string configPath, ManagementApi managementApi, ILogger<ConfigReloader> logger)
    {
        if (string.IsNullOrWhiteSpace(configPath))
            throw new ArgumentException("configPath must be set.", nameof(configPath));
        ArgumentNullException.ThrowIfNull(managementApi);
        ArgumentNullException.ThrowIfNull(logger);

        _configPath = configPath;
        _managementApi = managementApi;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(_configPath);
        var file = Path.GetFileName(_configPath);

        // FileSystemWatcher needs an existing directory; if it is missing (e.g. an unusual
        // deployment) hot-reload is simply disabled rather than crashing the host on start.
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            _logger.LogWarning(
                "Config hot-reload disabled: config directory '{Dir}' does not exist.", dir ?? "<empty>");
            return Task.CompletedTask;
        }

        _watcher = new FileSystemWatcher(dir)
        {
            Filter = file,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        // Most editors trigger Changed (LastWrite). Some do atomic save-as (write a temp file then
        // rename onto the target), so also react to Created/Renamed to catch those workflows.
        // Wire handlers BEFORE enabling events so no change between init and subscription is missed.
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Renamed += OnFileChanged;
        _watcher.EnableRaisingEvents = true;

        // One debounce timer, rearmed (never auto-recurring): fires exactly once, DebounceMs after
        // the last change event, so a burst collapses into a single reload.
        _debounceTimer = new Timer(_ => TriggerReload(), null, Timeout.Infinite, Timeout.Infinite);

        _logger.LogInformation("Config hot-reload enabled: watching '{Path}'.", _configPath);
        return Task.CompletedTask;
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Rearm the debounce window. Timer.Change is thread-safe; rapid bursts keep pushing the fire
        // time out so only the final event triggers a reload.
        try
        {
            _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // StopAsync ran between the event firing and the rearm — ignore, shutting down.
        }
    }

    private void TriggerReload()
    {
        // At most one reload at a time: a slow reload (large YAML) should not overlap with a second
        // one triggered by another save during the first. ApplyConfig is itself thread-safe, but the
        // guard keeps the log output and the load+validate sequence coherent.
        if (Interlocked.CompareExchange(ref _reloading, 1, 0) != 0)
            return;

        try
        {
            Reload();
        }
        finally
        {
            Volatile.Write(ref _reloading, 0);
        }
    }

    /// <summary>
    /// Loads, validates, and applies the config from disk. Extracted from the FileSystemWatcher path
    /// so the logic is directly unit-testable (a test points the reloader at a temp file and calls
    /// this). Never throws: any error (parse failure, validation failure, apply failure) is logged
    /// and the previous config stays in effect — the service must not be disrupted by a bad edit.
    /// </summary>
    internal void Reload()
    {
        try
        {
            _logger.LogInformation("Reloading config from '{Path}'...", _configPath);

            var newConfig = ConfigLoader.Load(_configPath);
            newConfig.Validate();

            _managementApi.ApplyConfig(newConfig);

            // Soft fields (TrustedProxies / CORS / rate & concurrency limits) are now live. Everything
            // else (Bgp.*, Peers, ApiPort, PrefixSources, RipeStat, communities) is baked into the
            // running sessions / listener and needs a restart — state that explicitly so the operator
            // is not surprised that editing e.g. HoldTime did nothing.
            _logger.LogInformation(
                "Config reloaded from '{Path}' (soft fields applied). " +
                "BGP/Peers/ApiPort/PrefixSources/communities changes require a restart.",
                _configPath);
        }
        catch (Exception ex)
        {
            // Strict-YAML (#102) + Validate (#89) run above; any failure here leaves the previous
            // config fully in effect. Do NOT rethrow — crashing here would tear the service down,
            // defeating the whole point of hot-reload.
            _logger.LogError(ex, "Config reload failed, keeping previous config: {Message}", ex.Message);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        if (_watcher is not null)
            _watcher.EnableRaisingEvents = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnFileChanged;
            _watcher.Created -= OnFileChanged;
            _watcher.Renamed -= OnFileChanged;
            _watcher.Dispose();
        }
        _debounceTimer?.Dispose();
    }
}
