using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.Sqlite
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
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureFetchedUrl",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PictureUpdatedAt",
                table: "Groups",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "PictureContent",
                table: "GroupMembers",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureContentType",
                table: "GroupMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PictureFetchedUrl",
                table: "GroupMembers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PictureUpdatedAt",
                table: "GroupMembers",
                type: "TEXT",
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
