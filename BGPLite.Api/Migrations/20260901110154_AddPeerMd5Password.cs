using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BGPLite.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerMd5Password : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Md5Password",
                table: "Peers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Md5Password",
                table: "Peers");
        }
    }
}
