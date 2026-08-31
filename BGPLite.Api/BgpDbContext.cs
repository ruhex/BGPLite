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
                // #264: converge the Peers COLUMN set before stamping — the converger migration
                // reconciles child tables and indexes, never Peers' own columns.
                ConvergeLegacyPeersColumns(connection);

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

    /// <summary>
    /// Expected <c>Peers</c> columns with the Init-migration DDL (#264). Nullable/defaulted only —
    /// a required column without a default cannot be ALTERed onto existing rows, and
    /// <see cref="ConvergeLegacyPeersColumns"/> refuses loudly for it instead of stamping an
    /// unusable schema.
    /// </summary>
    private static readonly (string Name, string Ddl)[] ExpectedPeersColumns =
    [
        ("Id", "\"Id\" TEXT NOT NULL"),
        ("Ip", "\"Ip\" TEXT NOT NULL"),
        ("Asn", "\"Asn\" INTEGER NULL"),
        ("Description", "\"Description\" TEXT NULL"),
        ("Status", "\"Status\" TEXT NOT NULL DEFAULT 'inactive'"),
        ("CreatedAt", "\"CreatedAt\" TEXT NOT NULL"),
        ("LastSessionAt", "\"LastSessionAt\" TEXT NULL"),
    ];

    /// <summary>
    /// #264: before the legacy stamp, converge the <c>Peers</c> column set. The converger MIGRATION
    /// reconciles child tables and indexes but never Peers' own columns, so an early EnsureCreated-era
    /// build missing one would stamp Init and fail its first raw write at runtime ("no such column").
    /// </summary>
    private static void ConvergeLegacyPeersColumns(System.Data.Common.DbConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(\"Peers\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existing.Add(reader.GetString(1)); // column 1 = name
        }

        foreach (var (name, ddl) in ExpectedPeersColumns)
        {
            if (existing.Contains(name))
                continue;
            if (ddl.Contains("NOT NULL") && !ddl.Contains("DEFAULT", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Legacy database is missing required column 'Peers.{name}' that cannot be added without a backfill. " +
                    "Restore the database from a backup or migrate it manually before upgrading.");
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE \"Peers\" ADD COLUMN {ddl}";
            alter.ExecuteNonQuery();
        }
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
