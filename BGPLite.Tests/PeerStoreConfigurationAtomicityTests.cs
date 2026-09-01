using BGPLite.Api;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Tests;

/// <summary>
/// #259: creating or updating a peer chained CreatePeer → SetSubscriptions → SetCustomPrefixes →
/// SetCustomAsns, each opening its own DbContext and transaction. #226/#227 made each individual
/// Set* atomic; the composition of them was not, so a failure part-way left a peer half-configured
/// and the client saw a 500 over an already-committed peer row.
/// <para>
/// The reported trigger is a duplicate CIDR, which violates the (PeerId, Prefix, PrefixLength)
/// primary key. The same hazard exists on every child collection — subscriptions are keyed
/// (PeerId, AsnListName) and custom ASNs (PeerId, Asn) — so a repeated list name or ASN throws
/// just as readily. A user assembling a prefix set in the management UI can produce any of the
/// three by pasting a list twice.
/// </para>
/// </summary>
public class PeerStoreConfigurationAtomicityTests
{
    private const string Ip = "203.0.113.30";
    private const uint Asn = 64530;

    /// <summary>
    /// The reported trigger: a repeated CIDR violates the <c>(PeerId, Prefix, PrefixLength)</c> key.
    /// Deduplicated rather than rejected — a set of prefixes means the same thing either way.
    /// </summary>
    [Fact]
    public async Task SavePeerConfiguration_DuplicatePrefixes_AreAcceptedIdempotently()
    {
        var (store, connection) = NewStore();
        using var _ = connection;

        var id = (await store.SavePeerConfigurationAsync(Ip, Asn, "dup prefixes",
            asnListNames: ["ru"],
            customPrefixes: [("10.0.0.0", 8), ("10.0.0.0", 8), ("192.0.2.0", 24)],
            customAsns: [64512])).Id;

        // A set of prefixes: announcing 10.0.0.0/8 twice is the same as once, so the duplicate is
        // dropped rather than rejected.
        Assert.Equal(["10.0.0.0/8", "192.0.2.0/24"], (await store.GetCustomPrefixesAsync(id)).Order().ToList());
        Assert.Equal(["ru"], await store.GetSubscriptionsAsync(id));
        Assert.Equal([64512u], await store.GetCustomAsnsAsync(id));
    }

    /// <summary>
    /// The collections the issue does not mention. Subscriptions and custom ASNs carry the same
    /// composite keys, so a pasted-twice list name or ASN threw exactly as a repeated CIDR did.
    /// </summary>
    [Fact]
    public async Task SavePeerConfiguration_DuplicateListNamesAndAsns_AreAcceptedIdempotently()
    {
        var (store, connection) = NewStore();
        using var _ = connection;

        var id = (await store.SavePeerConfigurationAsync(Ip, Asn, "dup lists and asns",
            asnListNames: ["ru", "ru", "cdn"],
            customPrefixes: [],
            customAsns: [64512, 64512, 64513])).Id;

        Assert.Equal(["cdn", "ru"], (await store.GetSubscriptionsAsync(id)).Order().ToList());
        Assert.Equal([64512u, 64513u], (await store.GetCustomAsnsAsync(id)).Order().ToList());
    }

    /// <summary>
    /// Read back through <c>LoadPeerRoutingView</c> — the same view the BGP send path uses, so this
    /// asserts the peer is complete where it actually matters, not merely in the store.
    /// </summary>
    [Fact]
    public async Task SavePeerConfiguration_AppliesEveryPartOrNothing()
    {
        var (store, connection) = NewStore();
        using var _ = connection;

        var id = (await store.SavePeerConfigurationAsync(Ip, Asn, "full",
            asnListNames: ["ru"],
            customPrefixes: [("10.0.0.0", 8)],
            customAsns: [64512])).Id;

        // The failure #259 describes is a peer row committed with its child collections missing.
        // Assert the whole configuration is readable through the same view the send path uses.
        var view = await store.LoadPeerRoutingViewAsync(Ip, Asn);
        Assert.NotNull(view);
        Assert.Equal(["ru"], view!.Subscriptions);
        Assert.Equal(["10.0.0.0/8"], view.CustomPrefixes);
        Assert.Equal([64512u], view.CustomAsns);
        Assert.Equal(id, view.PeerId);
    }

    /// <summary>
    /// The property #259 is actually about: the whole save is ONE transaction, not four DbContexts
    /// and three commits. Asserted by counting the transactions EF actually opens rather than by
    /// forcing a failure — no production test hook, and it fails the moment someone re-splits the
    /// composition into separate Set* calls.
    /// </summary>
    [Fact]
    public async Task SavePeerConfiguration_RunsInASingleTransaction()
    {
        var (store, connection, transactions) = NewStoreCountingTransactions();
        using var _ = connection;

        transactions.Clear();
        await store.SavePeerConfigurationAsync(Ip, Asn, "one transaction",
            asnListNames: ["ru", "cdn"],
            customPrefixes: [("10.0.0.0", 8), ("192.0.2.0", 24)],
            customAsns: [64512, 64513]);

        Assert.Single(transactions);
    }

    /// <summary>
    /// The update path carries the same guarantee as the create path.
    /// </summary>
    [Fact]
    public async Task UpdatePeerConfiguration_RunsInASingleTransaction()
    {
        var (store, connection, transactions) = NewStoreCountingTransactions();
        using var _ = connection;
        var id = (await store.SavePeerConfigurationAsync(Ip, Asn, "initial",
            asnListNames: ["ru"], customPrefixes: [("10.0.0.0", 8)], customAsns: [64512])).Id;

        transactions.Clear();
        await store.UpdatePeerConfigurationAsync(id, "changed",
            asnListNames: ["cdn"],
            customPrefixes: [("192.0.2.0", 24)],
            customAsns: [64514]);

        Assert.Single(transactions);
    }

    /// <summary>
    /// PATCH semantics: <c>null</c> means "leave this alone", while an empty list means "clear it".
    /// </summary>
    [Fact]
    public async Task UpdatePeerConfiguration_NullsLeaveTheExistingValueAlone()
    {
        var (store, connection) = NewStore();
        using var _ = connection;
        var id = (await store.SavePeerConfigurationAsync(Ip, Asn, "initial",
            asnListNames: ["ru"], customPrefixes: [("10.0.0.0", 8)], customAsns: [64512])).Id;

        // Only the prefixes are supplied; the rest must survive untouched, matching the PATCH
        // semantics the management API exposes.
        await store.UpdatePeerConfigurationAsync(id, description: null,
            asnListNames: null,
            customPrefixes: [("192.0.2.0", 24), ("192.0.2.0", 24)],
            customAsns: null);

        Assert.Equal(["192.0.2.0/24"], await store.GetCustomPrefixesAsync(id));
        Assert.Equal(["ru"], await store.GetSubscriptionsAsync(id));
        Assert.Equal([64512u], await store.GetCustomAsnsAsync(id));
        Assert.Equal("initial", (await store.GetDbPeerByIdAsync(id))!.Description);
    }

    /// <summary>A store over an in-memory SQLite DB that records every transaction EF opens.</summary>
    private static (PeerStore Store, SqliteConnection Connection, List<string> Transactions) NewStoreCountingTransactions()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var transactions = new List<string>();
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite(connection)
            .LogTo(line => { lock (transactions) transactions.Add(line); },
                   [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.TransactionStarted])
            .Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        return (new PeerStore(new StaticOptionsFactory(options)), connection, transactions);
    }

    /// <summary>A store over an in-memory SQLite DB, so composite keys and transactions are real.</summary>
    private static (PeerStore Store, SqliteConnection Connection) NewStore()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<BgpDbContext>().UseSqlite(connection).Options;
        using (var boot = new BgpDbContext(options))
            BgpDbContext.Initialize(boot);
        return (new PeerStore(new StaticOptionsFactory(options)), connection);
    }

    private sealed class StaticOptionsFactory(DbContextOptions<BgpDbContext> options) : IDbContextFactory<BgpDbContext>
    {
        /// <summary>Hands out contexts over the one open connection, so the in-memory DB survives.</summary>
        public BgpDbContext CreateDbContext() => new(options);
    }
}
