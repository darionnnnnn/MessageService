using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MessageService.Data.Data.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AnonymousLabelUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Label",
                table: "AnonymousIdentities",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            // 修補既有重複的 (GroupId, Label) 資料：同組依 AssignedAt、UserId 排序，
            // 第一筆保持原樣，第二筆起修改為「原Label (n)」格式以解除衝突。
            migrationBuilder.Sql("""
                WITH Ranked AS (
                    SELECT Label,
                           ROW_NUMBER() OVER (PARTITION BY GroupId, Label ORDER BY AssignedAt, UserId) AS rn
                    FROM AnonymousIdentities
                )
                UPDATE Ranked
                SET Label = Label + ' (' + CAST(rn AS NVARCHAR(20)) + ')'
                WHERE rn > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AnonymousIdentities_GroupId_Label",
                table: "AnonymousIdentities",
                columns: new[] { "GroupId", "Label" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AnonymousIdentities_GroupId_Label",
                table: "AnonymousIdentities");

            migrationBuilder.AlterColumn<string>(
                name: "Label",
                table: "AnonymousIdentities",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            // 降版只移除唯一索引與還原欄位型別，不還原被修補過的 Label：
            // 修補後的 Label（如「原Label (2)」）已成為有效代號，
            // 還原既有資料可能再次造成名稱衝突，且無法精準還原修補前的原始意圖。
        }
    }
}
