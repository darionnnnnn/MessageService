using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
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
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MachineName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OutboxPending = table.Column<long>(type: "bigint", nullable: true),
                    OutboxOldestAgeSeconds = table.Column<double>(type: "float", nullable: true),
                    EncryptionKeyFingerprint = table.Column<string>(type: "nvarchar(max)", nullable: true)
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
