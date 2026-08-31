using BGPLite.Routing;

namespace BGPLite.Tests;

public class RouteTableTests
{
    [Fact]
    public void AddOrUpdate_NewRoute_ReturnsTrue()
    {
        var table = new RouteTable();
        var route = new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x01020304 };

        Assert.True(table.AddOrUpdate(route));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void AddOrUpdate_ExistingRoute_ReturnsFalse()
    {
        var table = new RouteTable();
        var route1 = new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x01020304 };
        var route2 = new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x05060708 };

        table.AddOrUpdate(route1);
        Assert.False(table.AddOrUpdate(route2));
        Assert.Equal(1, table.Count);

        var stored = table.Get(0xC0A80000, 24);
        Assert.Equal(0x05060708u, stored!.NextHop);
    }

    [Fact]
    public void Remove_ExistingRoute_ReturnsTrue()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x01020304 });

        Assert.True(table.Remove(0xC0A80000, 24));
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Remove_NonExistingRoute_ReturnsFalse()
    {
        var table = new RouteTable();
        Assert.False(table.Remove(0xC0A80000, 24));
    }

    [Fact]
    public void GetAll_ReturnsAllRoutes()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x01020304 });
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 0x05060708 });

        var routes = table.GetAll();
        Assert.Equal(2, routes.Count);
    }

    [Fact]
    public void Clear_RemovesAllRoutes()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 0x01020304 });
        table.Clear();
        Assert.Equal(0, table.Count);
    }

    // ---- #313: bulk removal by owner (session close) ----

    /// <summary>Scoped by owner: one session's entries go, nobody else's does.</summary>
    [Fact]
    public void RemoveAllOwnedBy_RemovesOnlyThatOwnersEntries()
    {
        var table = new RouteTable();
        var leaving = new object();
        var staying = new object();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 }, leaving);
        table.AddOrUpdate(new Route { Prefix = 0x0A010000, PrefixLength = 16, NextHop = 1 }, leaving);
        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 2 }, staying);
        table.AddOrUpdate(new Route { Prefix = 0xC0000200, PrefixLength = 24, NextHop = 3 });   // the seed

        Assert.Equal(2, table.RemoveAllOwnedBy(leaving));

        Assert.Equal(2, table.Count);
        Assert.NotNull(table.Get(0xC0A80000, 24));
        Assert.NotNull(table.Get(0xC0000200, 24));   // unowned entries are nobody's to flush
    }

    /// <summary>
    /// The compare-and-remove rule from #289 in bulk form: <c>AddOrUpdate</c> transfers ownership, so
    /// a prefix the former owner announced but a later announcement replaced belongs to the new owner
    /// and must survive the former owner's close.
    /// </summary>
    [Fact]
    public void RemoveAllOwnedBy_SkipsEntriesTakenOverByAnotherOwner()
    {
        var table = new RouteTable();
        var first = new object();
        var second = new object();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 }, first);
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 2 }, second);

        Assert.Equal(0, table.RemoveAllOwnedBy(first));
        Assert.Equal(2u, table.Get(0x0A000000, 8)!.NextHop);
    }

    /// <summary>An owner that installed nothing removes nothing — the close path runs for every session.</summary>
    [Fact]
    public void RemoveAllOwnedBy_UnknownOwner_RemovesNothing()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        Assert.Equal(0, table.RemoveAllOwnedBy(new object()));
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void RemoveAllOwnedBy_NullOwner_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new RouteTable().RemoveAllOwnedBy(null!));

    /// <summary>
    /// #343: Count is a maintained counter, not ConcurrentDictionary.Count. Every mutation path
    /// must adjust it exactly once — a drift on ANY path desynchronizes the metric and /api/routes
    /// totals forever (there is no re-sync once quiescent).
    /// </summary>
    [Fact]
    public void Count_TracksEveryMutationPath()
    {
        var table = new RouteTable();
        var owner = new object();
        Assert.Equal(0, table.Count);

        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 1 });        // new +1
        Assert.Equal(1, table.Count);
        table.AddOrUpdate(new Route { Prefix = 0xC0A80000, PrefixLength = 24, NextHop = 2 });        // replace ±0
        Assert.Equal(1, table.Count);
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 }, owner);  // new +1
        table.AddOrUpdate(new Route { Prefix = 0x0B000000, PrefixLength = 8, NextHop = 1 }, owner);  // new +1
        Assert.Equal(3, table.Count);

        Assert.False(table.Remove(0x7F000000, 8));                                                  // miss ±0
        Assert.False(table.RemoveOwnedBy(0x0A000000, 8, new object()));                              // wrong owner ±0
        Assert.Equal(3, table.Count);

        Assert.True(table.RemoveOwnedBy(0x0A000000, 8, owner));                                     // owned −1
        Assert.Equal(2, table.Count);
        Assert.True(table.Remove(0xC0A80000, 24));                                                  // unconditional −1
        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.RemoveAllOwnedBy(owner));                                             // flush −1
        Assert.Equal(0, table.Count);
    }

    /// <summary>#343: Clear must reset the maintained count (and keep accepting traffic after).</summary>
    [Fact]
    public void Clear_ResetsMaintainedCount()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });
        table.AddOrUpdate(new Route { Prefix = 0x0B000000, PrefixLength = 8, NextHop = 1 });
        table.AddOrUpdate(new Route { Prefix = 0x0C000000, PrefixLength = 8, NextHop = 1 });

        table.Clear();

        Assert.Equal(0, table.Count);
        table.AddOrUpdate(new Route { Prefix = 0x0D000000, PrefixLength = 8, NextHop = 1 });
        Assert.Equal(1, table.Count);
    }

    /// <summary>
    /// #343: under concurrent mixed mutations the counter must converge to the dictionary's actual
    /// contents once drained — the 1:1 mutation↔transition invariant. Keys are worker-private, so
    /// the expected survivors are deterministic: each worker keeps its odd-index keys (added, then
    /// replaced ±0) and loses its even-index ones (added, then removed).
    /// </summary>
    [Fact]
    public async Task Count_MixedConcurrentMutations_ExactAfterDrain()
    {
        var table = new RouteTable();
        const int workers = 8, keys = 500;

        var tasks = Enumerable.Range(0, workers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < keys; i++)
            {
                var prefix = (uint)(0x0A000000 + w * keys + i);
                table.AddOrUpdate(new Route { Prefix = prefix, PrefixLength = 24, NextHop = 1 });
                if ((i & 1) == 0)
                    table.Remove(prefix, 24);
                else
                    table.AddOrUpdate(new Route { Prefix = prefix, PrefixLength = 24, NextHop = 2 });
            }
        })).ToArray();
        await Task.WhenAll(tasks);

        const int expected = workers * keys / 2;        // odd-index keys survive per worker
        Assert.Equal(expected, table.GetAll().Count);   // dictionary truth
        Assert.Equal(expected, table.Count);            // maintained counter converged to it
    }

    /// <summary>
    /// #346 (CodeRabbit): TryAdd returning false does NOT guarantee the key still exists at the
    /// replacement write — a concurrent remove in that window used to re-insert the entry via the
    /// plain indexer without incrementing the maintained count, drifting it permanently (negative
    /// after enough removals). Writers and removers hammering the SAME small key set hit that
    /// window constantly. Interleaving-dependent, so a deterministic red is impossible — this is
    /// the statistical catcher: after drain the counter must equal the dictionary's contents.
    /// </summary>
    [Fact]
    public async Task Count_SameKeyConcurrentAddReplaceRemove_ExactAfterDrain()
    {
        var table = new RouteTable();
        const int keys = 16, iterations = 3000, writers = 4, removers = 4;

        var workers = new List<Task>();
        for (var w = 0; w < writers; w++)
            workers.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                {
                    var prefix = (uint)(0x0A000000 + i % keys);
                    table.AddOrUpdate(new Route { Prefix = prefix, PrefixLength = 24, NextHop = (uint)i });
                }
            }));
        for (var r = 0; r < removers; r++)
            workers.Add(Task.Run(() =>
            {
                for (var i = 0; i < iterations; i++)
                    table.Remove((uint)(0x0A000000 + i % keys), 24);
            }));
        await Task.WhenAll(workers);

        var actual = table.GetAll().Count;
        Assert.InRange(actual, 0, keys);   // sanity: some subset of the key set survived
        Assert.Equal(actual, table.Count); // counter == dictionary truth (no drift, never negative)
    }
}
