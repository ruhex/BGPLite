using BGPLite.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BGPLite.Api;

/// <summary>
/// Loads persisted TCP-MD5 credentials before the BGP hosted service binds its listener.
/// Any database or kernel failure aborts startup instead of admitting unsigned peer connections.
/// </summary>
public sealed class PeerMd5BootstrapService(
    PeerStore store,
    ISessionManager sessionManager,
    ILogger<PeerMd5BootstrapService> logger) : IHostedService
{
    /// <summary>Groups persisted credentials by source address, resolves shared-IP conflicts, and arms each key.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var armed = 0;
        foreach (var group in (await store.GetPeerMd5CredentialsAsync(cancellationToken))
                     .GroupBy(c => c.Ip).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var key = ManagementApi.ResolveSharedIpKey(group.Key, [.. group], logger);
            if (key is null) continue;
            sessionManager.SetPeerMd5Key(group.Key, key);
            armed++;
        }
        if (armed > 0)
            logger.LogInformation("TCP-MD5 armed for {Count} peer source address(es)", armed);
    }

    /// <summary>Leaves key teardown to BgpServer, which owns the listener and armed-key state.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
