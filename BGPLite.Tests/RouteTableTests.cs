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

    // ---- #14 phase 3: longest-prefix-match lookup ----

    [Fact]
    public void GetLongestPrefixMatch_MostSpecificPrefixWins()
    {
        var table = new RouteTable();
        var lessSpecific = new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 };
        var moreSpecific = new Route { Prefix = 0x0A010000, PrefixLength = 16, NextHop = 2 };
        table.AddOrUpdate(lessSpecific);
        table.AddOrUpdate(moreSpecific);

        Assert.Same(moreSpecific, table.GetLongestPrefixMatch(0x0A010203)); // inside both → /16
        Assert.Same(lessSpecific, table.GetLongestPrefixMatch(0x0A020304)); // inside /8 only
    }

    [Fact]
    public void GetLongestPrefixMatch_NoMatch_ReturnsNull()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        Assert.Null(table.GetLongestPrefixMatch(0xC0A80001)); // 192.168.0.1 — outside 10/8
    }

    [Fact]
    public void GetLongestPrefixMatch_EmptyTable_ReturnsNull()
    {
        Assert.Null(new RouteTable().GetLongestPrefixMatch(0x0A000001));
    }

    [Fact]
    public void GetLongestPrefixMatch_DefaultRoute_CatchesEverything()
    {
        var table = new RouteTable();
        var defaultRoute = new Route { Prefix = (UInt128)0, PrefixLength = 0, NextHop = 1 };
        table.AddOrUpdate(defaultRoute);

        Assert.Same(defaultRoute, table.GetLongestPrefixMatch(0xC0A80102));
    }

    [Fact]
    public void GetLongestPrefixMatch_RemovedMoreSpecific_FallsBackToLessSpecific()
    {
        var table = new RouteTable();
        var lessSpecific = new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 };
        table.AddOrUpdate(lessSpecific);
        table.AddOrUpdate(new Route { Prefix = 0x0A010000, PrefixLength = 16, NextHop = 2 });
        table.Remove(0x0A010000, 16);

        Assert.Same(lessSpecific, table.GetLongestPrefixMatch(0x0A010203));
    }

    [Fact]
    public void GetLongestPrefixMatch_Ipv6_MostSpecificPrefixWins()
    {
        var table = new RouteTable();
        var lessSpecific = new Route { Prefix = V6Net(0x2001, 0xDB8), IsIpv4 = false, PrefixLength = 32, NextHop = 1 };
        var moreSpecific = new Route { Prefix = V6Net(0x2001, 0xDB8, 1), IsIpv4 = false, PrefixLength = 48, NextHop = 2 };
        table.AddOrUpdate(lessSpecific);
        table.AddOrUpdate(moreSpecific);

        // Inside both → /48; inside the /32 but outside the /48 → /32; outside both → null.
        var inBoth = V6Net(0x2001, 0xDB8, 1) + 1;
        var inWide = V6Net(0x2001, 0xDB8, 2);
        Assert.Same(moreSpecific, table.GetLongestPrefixMatch(inBoth, isIpv4: false));
        Assert.Same(lessSpecific, table.GetLongestPrefixMatch(inWide, isIpv4: false));
        Assert.Null(table.GetLongestPrefixMatch(V6Net(0x2001, 0xDB9), isIpv4: false));
    }

    [Fact]
    public void GetLongestPrefixMatch_Ipv4_HostRouteBoundary()
    {
        // /32 is the family length maximum: the probe must hit the exact host route and
        // nothing one address away.
        var table = new RouteTable();
        var host = new Route { Prefix = 0x0A000001, PrefixLength = 32, NextHop = 1 };
        table.AddOrUpdate(host);

        Assert.Same(host, table.GetLongestPrefixMatch(0x0A000001));
        Assert.Null(table.GetLongestPrefixMatch(0x0A000002));
    }

    [Fact]
    public void GetLongestPrefixMatch_Ipv6_HostAndDefaultBoundaries()
    {
        // Both IPv6 length extremes in one table: a /128 host route (probe starts at the
        // family maximum) and ::/0 (probe ends at the minimum) — the address between them
        // falls through 128..1 to the default.
        var table = new RouteTable();
        var hostRoute = new Route { Prefix = V6Net(0x2001, 0xDB8, 1) + 5, IsIpv4 = false, PrefixLength = 128, NextHop = 1 };
        var defaultRoute = new Route { Prefix = (UInt128)0, IsIpv4 = false, PrefixLength = 0, NextHop = 2 };
        table.AddOrUpdate(hostRoute);
        table.AddOrUpdate(defaultRoute);

        Assert.Same(hostRoute, table.GetLongestPrefixMatch(V6Net(0x2001, 0xDB8, 1) + 5, isIpv4: false));
        Assert.Same(defaultRoute, table.GetLongestPrefixMatch(V6Net(0x2001, 0xDB9), isIpv4: false));
    }

    [Fact]
    public void GetLongestPrefixMatch_Ipv6_NeverMatchesIpv4Entries()
    {
        // Family-scoped: the IPv4 key carries IsIpv4 = true, so an IPv6 address whose 128-bit
        // value numerically contains the IPv4 network must not match it.
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = 0x0A000000, PrefixLength = 8, NextHop = 1 });

        Assert.Null(table.GetLongestPrefixMatch(0x0A000001, isIpv4: false));
    }

    [Fact]
    public void GetLongestPrefixMatch_Ipv4_NeverMatchesIpv6Entries()
    {
        var table = new RouteTable();
        table.AddOrUpdate(new Route { Prefix = V6Net(0x2001, 0xDB8), IsIpv4 = false, PrefixLength = 32, NextHop = 1 });

        Assert.Null(table.GetLongestPrefixMatch(0x20010DB8, isIpv4: true));
    }

    /// <summary>Composes an IPv6 network address from its leading hextets (the rest are zero):
    /// <c>V6Net(0x2001, 0xDB8, 1)</c> is 2001:db8:1::.</summary>
    private static UInt128 V6Net(ushort g0, ushort g1, ushort g2 = 0) =>
        ((UInt128)g0 << 112) | ((UInt128)g1 << 96) | ((UInt128)g2 << 80);
}
