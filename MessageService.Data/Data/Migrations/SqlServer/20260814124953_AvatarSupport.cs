using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AvatarSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "Groups",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                table: "Groups",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureFetchedUrl",
                table: "Groups",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PictureUpdatedAt",
                table: "Groups",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "GroupMembers",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                table: "GroupMembers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureFetchedUrl",
                table: "GroupMembers",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PictureUpdatedAt",
                table: "GroupMembers",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PictureContent",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PictureContentType",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PictureFetchedUrl",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PictureUpdatedAt",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "PictureContent",
                table: "GroupMembers");

            migrationBuilder.DropColumn(
                name: "PictureContentType",
                table: "GroupMembers");

            migrationBuilder.DropColumn(
                name: "PictureFetchedUrl",
                table: "GroupMembers");

            migrationBuilder.DropColumn(
                name: "PictureUpdatedAt",
                table: "GroupMembers");
        }
    }
}
