using System.Text.Json;

namespace BGPLite.Tests.Integration;

/// <summary>
/// Process-restart scenario (regression checklist: "Does API state survive server restart?"):
/// peers created against one stack instance must survive a full stop/restart over the same SQLite
/// file, with identical configuration and identical prefix export — the regression the prod
/// stale-image incident made visible.
/// </summary>
public sealed class RestartPersistenceE2eTests
{
    [Fact]
    public async Task PeerState_SurvivesFullRestart_WithSameExport()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "bgplite-e2e-restart-" + Guid.NewGuid().ToString("N") + ".db");
        var first = await RouteServerFixture.StartAsync(
            dbPath: dbPath,
            seed: p => p.AsnPrefixes[714] = ["17.253.14.0/24", "17.250.16.0/20"]);
        try
        {
            // Peer at 127.0.0.1 seeded via the store so the BGP client can reconnect after restart
            // (the API boundary rejects loopback peer IPs — production policy).
            var saved = await first.Store.SavePeerConfigurationAsync(
                "127.0.0.1", 65010, "restart-e2e", ["apple"], [FixtureScenario.SplitCidr("103.10.0.0/16")], []);
            var peerId = saved.Id;
            var before = await ExportedAsync(first, peerId);

            await first.RestartAsync();
            Assert.Equal(before, await ExportedAsync(first, peerId));

            var detail = await first.GetJsonAsync($"/api/peers/{peerId}");
            Assert.Equal(65010, detail.GetProperty("asn").GetInt32());
            Assert.Equal(["apple"], detail.GetProperty("lists").EnumerateArray().Select(e => e.GetString()!));
            Assert.Equal(["103.10.0.0/16"], detail.GetProperty("customPrefixes").EnumerateArray().Select(e => e.GetString()!));

            // The restarted server accepts BGP sessions and serves the same route set again.
            using var client = new BgpTestClient(65010, routerId: 0x0A000001);
            await client.ConnectAndEstablishAsync(first.BgpPort);
            var dump = await client.CollectDumpAsync();
            Assert.Contains(dump.SelectMany(u => u.Announced), r => r.Prefix == "103.10.0.0/16");
        }
        finally
        {
            await first.DisposeAsync();
            try
            {
                if (File.Exists(dbPath)) File.Delete(dbPath);
                if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal");
                if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm");
            }
            catch { /* best-effort cleanup */ }
        }
    }

    private static async Task<string[]> ExportedAsync(RouteServerFixture server, string peerId)
    {
        var exported = await server.GetJsonAsync($"/api/peers/{peerId}/prefixes?format=json");
        return exported.EnumerateArray().Select(e => e.GetString()!).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }
}
