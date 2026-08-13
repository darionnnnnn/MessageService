using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class SchemaHardeningRound1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MaskLandline",
                table: "ViewerSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MaskMobilePhone",
                table: "ViewerSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MaskNationalId",
                table: "ViewerSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MaskNhiCard",
                table: "ViewerSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "RetentionDays",
                table: "ViewerSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AlterColumn<string>(
                name: "DownloadStatus",
                table: "MessageContents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddColumn<int>(
                name: "FailedAttempts",
                table: "MessageContents",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastAttemptAt",
                table: "MessageContents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "GroupMessages",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MessageType",
                table: "GroupMessages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "GroupMessages",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.UpdateData(
                table: "ViewerSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "MaskLandline", "MaskMobilePhone", "MaskNationalId", "MaskNhiCard", "RetentionDays" },
                values: new object[] { true, true, true, true, 1095 });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupId_EventTimestamp",
                table: "GroupMessages",
                columns: new[] { "GroupId", "EventTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_GroupId_Id",
                table: "GroupMessages",
                columns: new[] { "GroupId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_GroupId_EventTimestamp",
                table: "GroupMessages");

            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_GroupId_Id",
                table: "GroupMessages");

            migrationBuilder.DropColumn(
                name: "MaskLandline",
                table: "ViewerSettings");

            migrationBuilder.DropColumn(
                name: "MaskMobilePhone",
                table: "ViewerSettings");

            migrationBuilder.DropColumn(
                name: "MaskNationalId",
                table: "ViewerSettings");

            migrationBuilder.DropColumn(
                name: "MaskNhiCard",
                table: "ViewerSettings");

            migrationBuilder.DropColumn(
                name: "RetentionDays",
                table: "ViewerSettings");

            migrationBuilder.DropColumn(
                name: "FailedAttempts",
                table: "MessageContents");

            migrationBuilder.DropColumn(
                name: "LastAttemptAt",
                table: "MessageContents");

            migrationBuilder.AlterColumn<string>(
                name: "DownloadStatus",
                table: "MessageContents",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                table: "GroupMessages",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "MessageType",
                table: "GroupMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "GroupId",
                table: "GroupMessages",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64);
        }
    }
}
