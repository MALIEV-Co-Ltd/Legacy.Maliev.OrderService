using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

public partial class AddOrderDeletionIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "OrderDeletionIntent",
            columns: table => new
            {
                OrderId = table.Column<int>(type: "integer", nullable: false),
                DeletionId = table.Column<Guid>(type: "uuid", nullable: false),
                RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                StatusCleanupCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OrderDeletionIntent", x => x.OrderId);
                table.CheckConstraint("CK_OrderDeletionIntent_AttemptCount", "\"AttemptCount\" >= 0");
            });
        migrationBuilder.CreateIndex(name: "IX_OrderDeletionIntent_DeletionId", table: "OrderDeletionIntent", column: "DeletionId", unique: true);
        migrationBuilder.CreateIndex(name: "IX_OrderDeletionIntent_PendingDue", table: "OrderDeletionIntent", columns: ["NextAttemptAtUtc", "OrderId"], filter: "\"CompletedAtUtc\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Deletion-intent migration is forward-only; retained recovery receipts must not be destroyed.");
}
