using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnonymousIdentities",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    IconKey = table.Column<string>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnonymousIdentities", x => new { x.GroupId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    PictureUrl = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupId, x.UserId });
                });

            migrationBuilder.CreateTable(
                name: "GroupMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebhookEventId = table.Column<string>(type: "TEXT", nullable: false),
                    LineMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    MessageType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: true),
                    StickerId = table.Column<string>(type: "TEXT", nullable: true),
                    PackageId = table.Column<string>(type: "TEXT", nullable: true),
                    EventTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    GroupName = table.Column<string>(type: "TEXT", nullable: true),
                    PictureUrl = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.GroupId);
                });

            migrationBuilder.CreateTable(
                name: "MaskKeywords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Keyword = table.Column<string>(type: "TEXT", nullable: false),
                    Replacement = table.Column<string>(type: "TEXT", nullable: true),
                    ApplyToAllGroups = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaskKeywords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAliases",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Alias = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAliases", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "ViewerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    NameDisplayMode = table.Column<string>(type: "TEXT", nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MaskNationalId = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaskMobilePhone = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaskLandline = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaskNhiCard = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageContents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupMessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", nullable: true),
                    DownloadStatus = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FailedAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MessageContents_GroupMessages_GroupMessageId",
                        column: x => x.GroupMessageId,
                        principalTable: "GroupMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaskKeywordGroups",
                columns: table => new
                {
                    MaskKeywordId = table.Column<int>(type: "INTEGER", nullable: false),
                    GroupId = table.Column<string>(type: "TEXT", nullable: false)
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
                columns: new[] { "Id", "MaskLandline", "MaskMobilePhone", "MaskNationalId", "MaskNhiCard", "NameDisplayMode", "RetentionDays" },
                values: new object[] { 1, true, true, true, true, "MaskMiddle", 1095 });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupId_EventTimestamp",
                table: "GroupMessages",
                columns: new[] { "GroupId", "EventTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupId_Id",
                table: "GroupMessages",
                columns: new[] { "GroupId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_WebhookEventId",
                table: "GroupMessages",
                column: "WebhookEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageContents_GroupMessageId",
                table: "MessageContents",
                column: "GroupMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnonymousIdentities");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "MaskKeywordGroups");

            migrationBuilder.DropTable(
                name: "MessageContents");

            migrationBuilder.DropTable(
                name: "UserAliases");

            migrationBuilder.DropTable(
                name: "ViewerSettings");

            migrationBuilder.DropTable(
                name: "MaskKeywords");

            migrationBuilder.DropTable(
                name: "GroupMessages");
        }
    }
}
