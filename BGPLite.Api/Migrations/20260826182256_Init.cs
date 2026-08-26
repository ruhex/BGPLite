using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BGPLite.Api.Migrations
{
    /// <inheritdoc />
    public partial class Init : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Peers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Ip = table.Column<string>(type: "TEXT", nullable: false),
                    Asn = table.Column<uint>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "inactive"),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSessionAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Peers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerCommunity",
                columns: table => new
                {
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    Community = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerCommunity", x => new { x.PeerId, x.Community });
                    table.ForeignKey(
                        name: "FK_PeerCommunity_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerCustomAsns",
                columns: table => new
                {
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    Asn = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerCustomAsns", x => new { x.PeerId, x.Asn });
                    table.ForeignKey(
                        name: "FK_PeerCustomAsns_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerCustomPrefix",
                columns: table => new
                {
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", nullable: false),
                    PrefixLength = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerCustomPrefix", x => new { x.PeerId, x.Prefix, x.PrefixLength });
                    table.ForeignKey(
                        name: "FK_PeerCustomPrefix_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerCustomSources",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: false),
                    Community = table.Column<string>(type: "TEXT", nullable: true),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerCustomSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerCustomSources_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerSubscription",
                columns: table => new
                {
                    PeerId = table.Column<string>(type: "TEXT", nullable: false),
                    AsnListName = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerSubscription", x => new { x.PeerId, x.AsnListName });
                    table.ForeignKey(
                        name: "FK_PeerSubscription_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeerCustomSources_PeerId",
                table: "PeerCustomSources",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "UX_Peers_Ip_Asn",
                table: "Peers",
                columns: new[] { "Ip", "Asn" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerCommunity");

            migrationBuilder.DropTable(
                name: "PeerCustomAsns");

            migrationBuilder.DropTable(
                name: "PeerCustomPrefix");

            migrationBuilder.DropTable(
                name: "PeerCustomSources");

            migrationBuilder.DropTable(
                name: "PeerSubscription");

            migrationBuilder.DropTable(
                name: "Peers");
        }
    }
}
