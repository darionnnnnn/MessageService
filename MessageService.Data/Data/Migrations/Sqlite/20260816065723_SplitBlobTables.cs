using MessageService.Data.Data.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
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
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false)
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
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false)
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
                    MessageContentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false)
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

            // 搬遷 SQL 與可重跑（NOT EXISTS）的理由見 SplitBlobTablesDataMove
            migrationBuilder.Sql(SplitBlobTablesDataMove.Sqlite);

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
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "GroupMembers",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Content",
                table: "MessageContents",
                type: "BLOB",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE Groups
                SET PictureContent = (
                    SELECT Content FROM GroupPictures
                    WHERE GroupPictures.GroupId = Groups.GroupId
                )
                WHERE EXISTS (
                    SELECT 1 FROM GroupPictures
                    WHERE GroupPictures.GroupId = Groups.GroupId
                );

                UPDATE GroupMembers
                SET PictureContent = (
                    SELECT Content FROM GroupMemberPictures
                    WHERE GroupMemberPictures.GroupId = GroupMembers.GroupId
                      AND GroupMemberPictures.UserId = GroupMembers.UserId
                )
                WHERE EXISTS (
                    SELECT 1 FROM GroupMemberPictures
                    WHERE GroupMemberPictures.GroupId = GroupMembers.GroupId
                      AND GroupMemberPictures.UserId = GroupMembers.UserId
                );

                UPDATE MessageContents
                SET Content = (
                    SELECT Content FROM MessageContentBlobs
                    WHERE MessageContentBlobs.MessageContentId = MessageContents.Id
                )
                WHERE EXISTS (
                    SELECT 1 FROM MessageContentBlobs
                    WHERE MessageContentBlobs.MessageContentId = MessageContents.Id
                );
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
