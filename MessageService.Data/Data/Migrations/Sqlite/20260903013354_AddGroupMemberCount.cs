using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddGroupMemberCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MemberCount",
                table: "Groups",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MemberCount",
                table: "Groups");
        }
    }
}
