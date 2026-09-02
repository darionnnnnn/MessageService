using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class AddHighlightRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS HighlightKeywordGroups;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS HighlightUsers;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS HighlightKeywords;");

            migrationBuilder.CreateTable(
                name: "HighlightKeywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Keyword = table.Column<string>(type: "TEXT", nullable: false),
                    ApplyToAllGroups = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightKeywords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HighlightUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HighlightKeywordGroups",
                columns: table => new
                {
                    HighlightKeywordId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightKeywordGroups", x => new { x.HighlightKeywordId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_HighlightKeywordGroups_HighlightKeywords_HighlightKeywordId",
                        column: x => x.HighlightKeywordId,
                        principalTable: "HighlightKeywords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HighlightKeywordGroups");

            migrationBuilder.DropTable(
                name: "HighlightUsers");

            migrationBuilder.DropTable(
                name: "HighlightKeywords");
        }
    }
}
