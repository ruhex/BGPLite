using BGPLite.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Api;

public class BgpDbContext : DbContext
{
    public DbSet<Peer> Peers => Set<Peer>();

    public BgpDbContext(DbContextOptions<BgpDbContext> options) : base(options) { }

    public static void Initialize(BgpDbContext db)
    {
        db.Database.EnsureCreated();
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS PeerCustomAsns (" +
            "PeerId TEXT NOT NULL, Asn INTEGER NOT NULL, " +
            "PRIMARY KEY (PeerId, Asn), " +
            "FOREIGN KEY (PeerId) REFERENCES Peers(Id) ON DELETE CASCADE)");

        // Peer identity is (Ip, Asn), not Ip alone, so several peers behind one source IP (distinct
        // AS) can coexist as separate rows (issue #19). EnsureCreated does not evolve an existing
        // schema, so migrate the index idempotently: drop the legacy Ip-only unique index and create
        // the composite one if it is missing. Existing data is already unique by Ip (the old index
        // enforced it), so (Ip, Asn) is unique on it as well — the CREATE cannot fail.
        db.Database.ExecuteSqlRaw("DROP INDEX IF EXISTS IX_Peers_Ip;");
        db.Database.ExecuteSqlRaw(
            "CREATE UNIQUE INDEX IF NOT EXISTS UX_Peers_Ip_Asn ON Peers (Ip, Asn);");

        db.Peers.Where(p => p.Status == "active").ExecuteUpdate(
            s => s.SetProperty(p => p.Status, "inactive"));
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Peer>(e =>
        {
            e.HasKey(p => p.Id);
            // Composite identity (Ip, Asn): distinct peers behind one source IP with different AS
            // must be separate rows (issue #19). Named so the idempotent migration in Initialize
            // can recreate it deterministically across fresh and existing databases.
            e.HasIndex(p => new { p.Ip, p.Asn }).IsUnique().HasDatabaseName("UX_Peers_Ip_Asn");
            e.Property(p => p.Status).HasDefaultValue("inactive");
        });

        model.Entity<PeerCommunity>(e =>
        {
            e.HasKey(c => new { c.PeerId, c.Community });
            e.HasOne(c => c.Peer).WithMany(p => p.Communities)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PeerSubscription>(e =>
        {
            e.HasKey(s => new { s.PeerId, s.AsnListName });
            e.HasOne(s => s.Peer).WithMany(p => p.Subscriptions)
                .HasForeignKey(s => s.PeerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PeerCustomPrefix>(e =>
        {
            e.HasKey(c => new { c.PeerId, c.Prefix, c.PrefixLength });
            e.HasOne(c => c.Peer).WithMany(p => p.CustomPrefixes)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PeerCustomAsn>(e =>
        {
            e.ToTable("PeerCustomAsns");
            e.HasKey(c => new { c.PeerId, c.Asn });
            e.HasOne(c => c.Peer).WithMany(p => p.CustomAsns)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
