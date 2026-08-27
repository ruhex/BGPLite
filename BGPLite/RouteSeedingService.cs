using BGPLite.Configuration;
using BGPLite.Contracts;
using BGPLite.Protocol;
using BGPLite.Providers;
using BGPLite.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite;

/// <summary>
/// #251: seeds the route table from the configured prefix sources and warms the RIPEstat cache as a
/// BACKGROUND task, so the BGP listener (:179) and the management API start immediately. Previously
/// both waited on the top-level <c>await WarmUpAsync()</c> — a hanging RIPEstat fetch (firewall DROP
/// × N ASNs × 3 attempts × 180 s) could delay every listener for hours.
/// <para>
/// Seeding order inside the background task: sources first — the local nets.txt fallback seeds in
/// milliseconds even under a total RIPE blackout — then the per-ASN cache warm-up. When seeding
/// completes, any session established in the meantime receives the full set as an unsolicited
/// UPDATE via <see cref="ISessionManager.RefreshAllEstablishedAsync"/> (the #214 push mechanism),
/// so an early peer first gets the local fallback and then the complete table.
/// </para>
/// <para>
/// Registered BEFORE the BGP server hosted service: hosted services start in registration order,
/// and although <see cref="StartAsync"/> returns immediately (listeners never wait on network I/O),
/// the ordering documents the intent — seeding begins before any session can possibly be accepted,
/// and the single task means seeding itself cannot race.
/// </para>
/// </summary>
internal sealed class RouteSeedingService(
    IPrefixService prefixService,
    IPrefixSourceService sources,
    RouteTable routeTable,
    AppConfig config,
    ISessionManager sessionManager,
    ILogger<RouteSeedingService> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _seedTask;

    public void Dispose() => _cts.Dispose();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Never block listener startup on network I/O — the whole point of #251.
        _seedTask = Task.Run(() => SeedAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_seedTask is null) return;
        try { await _seedTask.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { /* shutdown raced the seeding — fine */ }
        catch (Exception ex) { logger.LogError(ex, "Route seeding faulted during shutdown"); }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        var nextHop = BgpConstants.IPAddressToUint(config.Bgp.GetRouterIdAddress());
        try
        {
            foreach (var (source, prefixes) in await sources.LoadAllAsync(ct))
            {
                // Never throw during seeding (the ConfigCommunityResolver contract): a malformed
                // community degrades THIS source to untagged instead of aborting the loop — the
                // outer catch-all would otherwise skip every later source, WarmUpAsync, and the
                // final push. #328 made out-of-range communities throw instead of silently masking.
                var communities = Array.Empty<uint>();
                if (!string.IsNullOrEmpty(source.Community))
                {
                    try { communities = [CommunityCodec.Parse(source.Community!)]; }
                    catch (FormatException ex)
                    {
                        logger.LogWarning(ex, "Source '{Name}': invalid community '{Community}' — seeding untagged",
                            source.Name, source.Community);
                    }
                }

                foreach (var (prefix, length) in prefixes)
                {
                    routeTable.AddOrUpdate(new Route
                    {
                        Prefix = prefix,
                        PrefixLength = length,
                        NextHop = nextHop,
                        Communities = communities
                    });
                }

                // The suffix reflects the APPLIED tag, not the configured one — after a degrade
                // (Warning above) the configured string was invalid and the source went out untagged.
                logger.LogInformation("Source '{Name}': {Count} prefixes{Community}",
                    source.Name, prefixes.Count,
                    communities.Length == 0 ? "" : $" community={source.Community}");
            }

            logger.LogInformation("Loaded routes: {RouteCount} — warming RIPEstat cache", routeTable.Count);

            await prefixService.WarmUpAsync(ct);
            logger.LogInformation("Prefix cache warm — {RouteCount} routes on the wire", routeTable.Count);

            // Sessions established while seeding ran get the full set now (#214 push).
            await sessionManager.RefreshAllEstablishedAsync();
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Route seeding cancelled at shutdown — {RouteCount} routes were seeded", routeTable.Count);
        }
        catch (Exception ex)
        {
            // The server keeps serving whatever seeded (local fallback / stale-on-failure caches);
            // per-source load failures are absorbed by LoadAllAsync and community errors degrade
            // to untagged in the loop above — reaching here means seeding itself failed.
            logger.LogError(ex, "Route seeding failed — serving with {RouteCount} routes", routeTable.Count);
        }
    }
}
