using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddHighlightRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HighlightKeywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Keyword = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApplyToAllGroups = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightKeywords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HighlightUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HighlightUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HighlightKeywordGroups",
                columns: table => new
                {
                    HighlightKeywordId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(450)", nullable: false)
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
