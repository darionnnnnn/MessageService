using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddHostHeartbeats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostHeartbeats",
                columns: table => new
                {
                    Role = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MachineName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    OutboxPending = table.Column<long>(type: "INTEGER", nullable: true),
                    OutboxOldestAgeSeconds = table.Column<double>(type: "REAL", nullable: true),
                    EncryptionKeyFingerprint = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostHeartbeats", x => new { x.Role, x.MachineName });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostHeartbeats");
        }
    }
}
