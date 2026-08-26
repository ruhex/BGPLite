using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BGPLite.Api;

/// <summary>
/// Design-time context for `dotnet ef migrations` (#237). The real context is configured by the
/// host with the operator's dbPath; the tooling only needs the model + provider, so a throwaway
/// file path is fine — no database is created at design time.
/// </summary>
public sealed class BgpDbContextFactory : IDesignTimeDbContextFactory<BgpDbContext>
{
    public BgpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BgpDbContext>()
            .UseSqlite("DataSource=ef-design-time.db")
            .Options;
        return new BgpDbContext(options);
    }
}
