using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class SplitBlobTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupMemberPictures",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMemberPictures", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GroupMemberPictures_GroupMembers_GroupId_UserId",
                        columns: x => new { x.GroupId, x.UserId },
                        principalTable: "GroupMembers",
                        principalColumns: new[] { "GroupId", "UserId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupPictures",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPictures", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_GroupPictures_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MessageContentBlobs",
                columns: table => new
                {
                    MessageContentId = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageContentBlobs", x => x.MessageContentId);
                    table.ForeignKey(
                        name: "FK_MessageContentBlobs_MessageContents_MessageContentId",
                        column: x => x.MessageContentId,
                        principalTable: "MessageContents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO [GroupPictures] ([GroupId], [Content])
                SELECT [GroupId], [PictureContent]
                FROM [Groups]
                WHERE [PictureContent] IS NOT NULL;

                INSERT INTO [GroupMemberPictures] ([GroupId], [UserId], [Content])
                SELECT [GroupId], [UserId], [PictureContent]
                FROM [GroupMembers]
                WHERE [PictureContent] IS NOT NULL;

                INSERT INTO [MessageContentBlobs] ([MessageContentId], [Content])
                SELECT [Id], [Content]
                FROM [MessageContents]
                WHERE [Content] IS NOT NULL;
                """);

            migrationBuilder.DropColumn(
                name: "PictureContent",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PictureContent",
                table: "GroupMembers");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "MessageContents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "Groups",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "GroupMembers",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Content",
                table: "MessageContents",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE g
                SET g.[PictureContent] = gp.[Content]
                FROM [Groups] g
                INNER JOIN [GroupPictures] gp ON g.[GroupId] = gp.[GroupId];

                UPDATE gm
                SET gm.[PictureContent] = gmp.[Content]
                FROM [GroupMembers] gm
                INNER JOIN [GroupMemberPictures] gmp ON gm.[GroupId] = gmp.[GroupId] AND gm.[UserId] = gmp.[UserId];

                UPDATE mc
                SET mc.[Content] = mcb.[Content]
                FROM [MessageContents] mc
                INNER JOIN [MessageContentBlobs] mcb ON mc.[Id] = mcb.[MessageContentId];
                """);

            migrationBuilder.DropTable(
                name: "GroupMemberPictures");

            migrationBuilder.DropTable(
                name: "GroupPictures");

            migrationBuilder.DropTable(
                name: "MessageContentBlobs");
        }
    }
}
