using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.OrderStatus
{
    /// <inheritdoc />
    public partial class InitialOrderStatusPostgresCompatibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrderStatus",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatus", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "OrderStatusHasPossibleStatus",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderStatusID = table.Column<int>(type: "integer", nullable: false),
                    PossibleStatusID = table.Column<int>(type: "integer", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatusHasPossibleStatus", x => x.ID);
                    table.ForeignKey(
                        name: "FK_OrderStatusHasPossibleStatus_OrderStatus",
                        column: x => x.OrderStatusID,
                        principalTable: "OrderStatus",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_OrderStatusHasPossibleStatus_OrderStatus1",
                        column: x => x.PossibleStatusID,
                        principalTable: "OrderStatus",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateTable(
                name: "OrderStatusHistory",
                columns: table => new
                {
                    ID = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrderID = table.Column<int>(type: "integer", nullable: false),
                    OrderStatusID = table.Column<int>(type: "integer", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP"),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStatusHistory", x => x.ID);
                    table.ForeignKey(
                        name: "FK_OrderHasOrderStatus_OrderStatus",
                        column: x => x.OrderStatusID,
                        principalTable: "OrderStatus",
                        principalColumn: "ID");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHasPossibleStatus_OrderStatusID",
                table: "OrderStatusHasPossibleStatus",
                column: "OrderStatusID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHasPossibleStatus_PossibleStatusID",
                table: "OrderStatusHasPossibleStatus",
                column: "PossibleStatusID");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStatusHistory_OrderStatusID",
                table: "OrderStatusHistory",
                column: "OrderStatusID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderStatusHasPossibleStatus");

            migrationBuilder.DropTable(
                name: "OrderStatusHistory");

            migrationBuilder.DropTable(
                name: "OrderStatus");
        }
    }
}
