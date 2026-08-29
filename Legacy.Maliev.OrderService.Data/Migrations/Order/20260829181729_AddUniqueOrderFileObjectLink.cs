using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order
{
    /// <inheritdoc />
    public partial class AddUniqueOrderFileObjectLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "OrderFile" AS duplicate
                USING "OrderFile" AS retained
                WHERE duplicate."OrderID" = retained."OrderID"
                  AND duplicate."Bucket" = retained."Bucket"
                  AND duplicate."ObjectName" = retained."ObjectName"
                  AND duplicate."ID" > retained."ID";
                """);

            migrationBuilder.DropIndex(
                name: "IX_OrderFile_OrderID",
                table: "OrderFile");

            migrationBuilder.CreateIndex(
                name: "IX_OrderFile_OrderID_Bucket_ObjectName",
                table: "OrderFile",
                columns: new[] { "OrderID", "Bucket", "ObjectName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OrderFile_OrderID_Bucket_ObjectName",
                table: "OrderFile");

            migrationBuilder.CreateIndex(
                name: "IX_OrderFile_OrderID",
                table: "OrderFile",
                column: "OrderID");
        }
    }
}
