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
}
