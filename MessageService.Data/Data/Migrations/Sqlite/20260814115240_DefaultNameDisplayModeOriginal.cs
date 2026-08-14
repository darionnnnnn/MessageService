using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class DefaultNameDisplayModeOriginal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "ViewerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NameDisplayMode",
                value: "Original");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                table: "ViewerSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NameDisplayMode",
                value: "MaskMiddle");
        }
    }
}
