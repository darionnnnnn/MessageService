using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddHostHeartbeatChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "HostHeartbeats",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "HostHeartbeats");
        }
    }
}
