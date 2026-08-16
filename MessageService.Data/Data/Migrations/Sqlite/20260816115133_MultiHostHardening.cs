using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class MultiHostHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ClaimedAt",
                table: "MessageContents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "MessageContents",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages",
                column: "MessageType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "MessageContents");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "MessageContents");
        }
    }
}
