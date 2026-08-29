using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order
{
    /// <inheritdoc />
    public partial class AddDurableOrderOperationKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationKey",
                table: "Order",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Order_OperationKey",
                table: "Order",
                column: "OperationKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Order_OperationKey",
                table: "Order");

            migrationBuilder.DropColumn(
                name: "OperationKey",
                table: "Order");
        }
    }
}
