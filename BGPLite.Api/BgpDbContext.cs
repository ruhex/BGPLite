using BGPLite.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace BGPLite.Api;

public class BgpDbContext : DbContext
{
    public DbSet<Peer> Peers => Set<Peer>();

    public BgpDbContext(DbContextOptions<BgpDbContext> options) : base(options) { }

    /// <summary>
    /// Applies pending EF Migrations and deactivates all peers on startup (#237).
    /// <para>
    /// Fresh databases get the full schema via the Init migration. Databases from the
    /// EnsureCreated + ad-hoc-DDL era (a Peers table but no __EFMigrationsHistory) are stamped
    /// past Init — their schema predates it in unknown shapes — and the LegacyEnsureCreated
    /// migration converges any drift (missing tables, the legacy Ip-only index) with guarded
    /// DDL, preserving data. Each migration runs in its own transaction, so initialization can
    /// no longer leave a partially-applied schema.
    /// </para>
    /// </summary>
    public static void Initialize(BgpDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        bool TableExists(string name)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
            var param = cmd.CreateParameter();
            param.ParameterName = "$name";
            param.Value = name;
            cmd.Parameters.Add(param);
            return cmd.ExecuteScalar() is not null;
        }

        db.Database.OpenConnection();
        try
        {
            if (TableExists("Peers") && !TableExists("__EFMigrationsHistory"))
            {
                // Pre-Migrations deployment: create the history table and stamp Init as applied —
                // LegacyEnsureCreated (not stamped) then converges the residual drift via Migrate.
                var efVersion = typeof(DbContext).Assembly.GetName().Version!.ToString();
                db.Database.ExecuteSqlRaw(
                    "CREATE TABLE __EFMigrationsHistory (" +
                    "\"MigrationId\" TEXT NOT NULL CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY, " +
                    "\"ProductVersion\" TEXT NOT NULL);");
                db.Database.ExecuteSql(
                    $"INSERT INTO __EFMigrationsHistory (\"MigrationId\", \"ProductVersion\") VALUES ({InitMigrationId}, {efVersion})");
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }

        db.Database.Migrate();

        // Startup deactivation (#204 semantics preserved): sessions do not survive a restart, so
        // every peer starts inactive. A single ExecuteUpdate is atomic under SQLite.
        db.Peers.Where(p => p.Status == "active").ExecuteUpdate(
            s => s.SetProperty(p => p.Status, "inactive"));
    }

    private const string InitMigrationId = "20260826182256_Init";

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

        model.Entity<PeerCustomSource>(e =>
        {
            e.ToTable("PeerCustomSources");
            e.HasKey(c => c.Id);
            e.HasOne(c => c.Peer).WithMany(p => p.CustomSources)
                .HasForeignKey(c => c.PeerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
