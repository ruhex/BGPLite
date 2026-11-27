using System.Data.Common;
using BGPLite.Api;
using BGPLite.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BGPLite.Tests;

/// <summary>
/// <c>LoadPeerRoutingViewAsync</c> and <c>GetPeerDetailAsync</c> wrap their split-query SELECTs in a
/// transaction so they read one snapshot. Microsoft.Data.Sqlite opens that transaction with
/// <c>BEGIN IMMEDIATE</c> unless the isolation level is <c>ReadUncommitted</c> — and BEGIN IMMEDIATE
/// takes the database write lock, so every such read serialised against every writer for the whole
/// snapshot (a writer stalled for busy_timeout, then failed with SQLITE_BUSY).
/// <para>
/// Asserted on a real file database in WAL: a write issued while a read snapshot is open goes
/// straight through, and the read's later SELECTs still return the pre-write rows.
/// </para>
/// </summary>
public sealed class PeerStoreReadLockTests : IDisposable
{
    private const string Ip = "203.0.113.21";
    private const uint Asn = 64521;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bgplite-read-lock-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;

    public PeerStoreReadLockTests()
    {
        Directory.CreateDirectory(_directory);
        _connectionString = $"DataSource={Path.Combine(_directory, "peers.db")}";
    }

    public void Dispose()
    {
        // This database's pool only — ClearAllPools would reach into other classes' connections.
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Theory]
    [InlineData(true)]  // LoadPeerRoutingViewAsync
    [InlineData(false)] // GetPeerDetailAsync
    public async Task ReadSnapshot_BlocksNoWriter_AndHoldsAcrossTheSplit(bool routingView)
    {
        var park = new ParkBeforeSecondSelect();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(_connectionString)
            .AddInterceptors(new SqlitePragmasInterceptor(), park)
            .Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        var store = new PeerStore(new StaticOptionsFactory(options));
        var id = await store.CreatePeerAsync(Ip, Asn, "read-lock test");
        await store.SetSubscriptionsAsync(id, ["list-a", "list-b"]);
        await store.SetCustomPrefixesAsync(id, [("10.0.0.0", (byte)8)]);
        park.Armed = true; // seeding done — the next read is the one under test

        var read = routingView ? (Task)store.LoadPeerRoutingViewAsync(Ip, Asn) : store.GetPeerDetailAsync(id);
        // A split query reads the root (the peer row) first and every collection after it, so
        // parking before the second SELECT leaves all four collections unread when the write lands.
        await park.Parked.Task.WaitAsync(TimeSpan.FromSeconds(5)); // snapshot open, first SELECT done
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await store.SetSubscriptionsAsync(id, ["list-c"]);
            watch.Stop();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3),
                $"the write was blocked by the open read snapshot for {watch.Elapsed}");
        }
        finally
        {
            park.Release.TrySetResult();
        }

        // The read holds a snapshot opened before the write: its later SELECTs still show the old
        // subscriptions, while a fresh read sees the committed change.
        park.Armed = false;
        if (routingView)
        {
            var view = await (Task<PeerRoutingView?>)read;
            Assert.Equal(["list-a", "list-b"], view!.Subscriptions);
            Assert.Equal(["10.0.0.0/8"], view.CustomPrefixes);
        }
        else
        {
            var detail = await (Task<PeerDetailDto?>)read;
            Assert.Equal(["list-a", "list-b"], detail!.Subscriptions);
            Assert.Equal(["10.0.0.0/8"], detail.CustomPrefixes);
        }
        Assert.Equal(["list-c"], (await store.GetPeerDetailAsync(id))!.Subscriptions);
    }

    /// <summary>Parks the read between its first and second SELECT, so the snapshot is open while
    /// the test issues its write. One-shot, and only once armed.</summary>
    private sealed class ParkBeforeSecondSelect : DbCommandInterceptor
    {
        private int _selects;
        private int _parked;
        public volatile bool Armed;
        public TaskCompletionSource Parked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && Interlocked.Increment(ref _selects) == 2 && Interlocked.Exchange(ref _parked, 1) == 0)
            {
                Parked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        public BgpDbContext CreateDbContext() => new(options);
    }
}
