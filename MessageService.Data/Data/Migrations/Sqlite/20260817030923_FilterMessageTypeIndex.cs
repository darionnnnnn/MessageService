using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class FilterMessageTypeIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages",
                column: "MessageType",
                filter: "\"MessageType\" = 'sticker'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages",
                column: "MessageType");
        }
    }
}
