using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BGPLite.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerMaxPrefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxPrefix",
                table: "Peers",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxPrefix",
                table: "Peers");
        }
    }
}
