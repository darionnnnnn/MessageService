using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddViewerSettingsAndMasking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaskKeywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Keyword = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Replacement = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApplyToAllGroups = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskKeywords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAliases",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Alias = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAliases", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ViewerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameDisplayMode = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MaskKeywordGroups",
                columns: table => new
                {
                    MaskKeywordId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskKeywordGroups", x => new { x.MaskKeywordId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_MaskKeywordGroups_MaskKeywords_MaskKeywordId",
                        column: x => x.MaskKeywordId,
                        principalTable: "MaskKeywords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "ViewerSettings",
                columns: new[] { "Id", "NameDisplayMode" },
                values: new object[] { 1, "MaskMiddle" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaskKeywordGroups");

            migrationBuilder.DropTable(
                name: "UserAliases");

            migrationBuilder.DropTable(
                name: "ViewerSettings");

            migrationBuilder.DropTable(
                name: "MaskKeywords");
        }
    }
}
