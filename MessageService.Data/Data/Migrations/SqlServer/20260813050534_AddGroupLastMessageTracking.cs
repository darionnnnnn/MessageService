using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddGroupLastMessageTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastMessageAt",
                table: "Groups",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LastMessageId",
                table: "Groups",
                type: "bigint",
                nullable: true);

            // 回填：既有部署升級後，側欄改讀 Groups.LastMessageId/LastMessageAt（見
            // GroupsController），這裡不補的話任何「有訊息但頭貼快取還沒抓過（Groups 表
            // 從沒建過列）」的群組升級後會直接從側欄消失，要等到下一則訊息進來才會重新出現。
            // 兩段：先幫沒有 Groups 列的群組補 stub（UpdatedAt 給 SQL Server datetimeoffset
            // 最小值讓頭貼快取判定過期立刻刷新），再更新既有列。
            migrationBuilder.Sql("""
                INSERT INTO Groups (GroupId, UpdatedAt, LastMessageId, LastMessageAt)
                SELECT latest.GroupId, CAST('0001-01-01' AS datetimeoffset), latest.Id, latest.EventTimestamp
                FROM (
                    SELECT gm.GroupId, gm.Id, gm.EventTimestamp,
                           ROW_NUMBER() OVER (PARTITION BY gm.GroupId ORDER BY gm.Id DESC) AS rn
                    FROM GroupMessages gm
                ) latest
                WHERE latest.rn = 1
                  AND NOT EXISTS (SELECT 1 FROM Groups g WHERE g.GroupId = latest.GroupId);
                """);

            migrationBuilder.Sql("""
                UPDATE g
                SET g.LastMessageId = latest.Id, g.LastMessageAt = latest.EventTimestamp
                FROM Groups g
                INNER JOIN (
                    SELECT gm.GroupId, gm.Id, gm.EventTimestamp,
                           ROW_NUMBER() OVER (PARTITION BY gm.GroupId ORDER BY gm.Id DESC) AS rn
                    FROM GroupMessages gm
                ) latest ON g.GroupId = latest.GroupId AND latest.rn = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastMessageAt",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "LastMessageId",
                table: "Groups");
        }
    }
}
