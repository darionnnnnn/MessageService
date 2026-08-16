using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class MultiHostHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "MessageContents",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedBy",
                table: "MessageContents",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages",
                column: "MessageType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GroupMessages_MessageType",
                table: "GroupMessages");

            migrationBuilder.DropColumn(
                name: "ClaimedBy",
                table: "MessageContents");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "MessageContents");
        }
    }
}
