using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BGPLite.Api.Migrations
{
    /// <summary>
    /// Converges databases created by the pre-Migrations EnsureCreated + ad-hoc DDL era (#237).
    /// Every statement is guarded (IF EXISTS / IF NOT EXISTS), so this is a no-op on databases
    /// the Init migration created, while an EnsureCreated-era database (stamped past Init by
    /// <c>BgpDbContext.Initialize</c>) gets exactly the drift fixed: any missing table, the
    /// legacy Ip-only unique index, and the composite UX_Peers_Ip_Asn index (issue #19).
    /// </summary>
    public partial class LegacyEnsureCreated : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "PeerCommunity" (
                    "PeerId" TEXT NOT NULL,
                    "Community" INTEGER NOT NULL,
                    CONSTRAINT "PK_PeerCommunity" PRIMARY KEY ("PeerId", "Community"),
                    CONSTRAINT "FK_PeerCommunity_Peers_PeerId" FOREIGN KEY ("PeerId") REFERENCES "Peers" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "PeerCustomAsns" (
                    "PeerId" TEXT NOT NULL,
                    "Asn" INTEGER NOT NULL,
                    CONSTRAINT "PK_PeerCustomAsns" PRIMARY KEY ("PeerId", "Asn"),
                    CONSTRAINT "FK_PeerCustomAsns_Peers_PeerId" FOREIGN KEY ("PeerId") REFERENCES "Peers" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "PeerCustomPrefix" (
                    "PeerId" TEXT NOT NULL,
                    "Prefix" TEXT NOT NULL,
                    "PrefixLength" INTEGER NOT NULL,
                    CONSTRAINT "PK_PeerCustomPrefix" PRIMARY KEY ("PeerId", "Prefix", "PrefixLength"),
                    CONSTRAINT "FK_PeerCustomPrefix_Peers_PeerId" FOREIGN KEY ("PeerId") REFERENCES "Peers" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "PeerCustomSources" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_PeerCustomSources" PRIMARY KEY,
                    "PeerId" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Url" TEXT NOT NULL,
                    "Community" TEXT NULL,
                    "Active" INTEGER NOT NULL DEFAULT 0,
                    CONSTRAINT "FK_PeerCustomSources_Peers_PeerId" FOREIGN KEY ("PeerId") REFERENCES "Peers" ("Id") ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS "PeerSubscription" (
                    "PeerId" TEXT NOT NULL,
                    "AsnListName" TEXT NOT NULL,
                    CONSTRAINT "PK_PeerSubscription" PRIMARY KEY ("PeerId", "AsnListName"),
                    CONSTRAINT "FK_PeerSubscription_Peers_PeerId" FOREIGN KEY ("PeerId") REFERENCES "Peers" ("Id") ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS "IX_PeerCustomSources_PeerId" ON "PeerCustomSources" ("PeerId");

                DROP INDEX IF EXISTS "IX_Peers_Ip";

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Peers_Ip_Asn" ON "Peers" ("Ip", "Asn");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible converger: the guarded DDL only ever brings a legacy schema UP to the
            // Init shape, so there is nothing to undo.
        }
    }
}
