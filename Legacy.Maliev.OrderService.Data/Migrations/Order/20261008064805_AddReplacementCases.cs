using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order
{
    /// <inheritdoc />
    public partial class AddReplacementCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReplacementCase",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    OriginalsJson = table.Column<string>(type: "jsonb", nullable: false),
                    EvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    CommandsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReportedBy = table.Column<int>(type: "integer", nullable: false),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplacementCase", x => x.Id);
                    table.CheckConstraint("CK_ReplacementCase_Identity", "\"CustomerId\" > 0 AND \"ReportedBy\" > 0 AND \"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ReplacementAffectedOrder",
                columns: table => new
                {
                    CaseId = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplacementAffectedOrder", x => new { x.CaseId, x.OrderId });
                    table.ForeignKey(
                        name: "FK_ReplacementAffectedOrder_Order_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Order",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReplacementAffectedOrder_ReplacementCase_CaseId",
                        column: x => x.CaseId,
                        principalTable: "ReplacementCase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReplacementOperation",
                columns: table => new
                {
                    EmployeeId = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CaseId = table.Column<int>(type: "integer", nullable: false),
                    ResultRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplacementOperation", x => new { x.EmployeeId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_ReplacementOperation_ReplacementCase_CaseId",
                        column: x => x.CaseId,
                        principalTable: "ReplacementCase",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplacementAffectedOrder_OrderId",
                table: "ReplacementAffectedOrder",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplacementCase_CustomerId",
                table: "ReplacementCase",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplacementOperation_CaseId",
                table: "ReplacementOperation",
                column: "CaseId");

            migrationBuilder.Sql("""
                CREATE FUNCTION replacement_case_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Replacement case history cannot be deleted';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Revision" <> 1 OR NEW."CommandsJson" <> '[]'::jsonb THEN
                            RAISE EXCEPTION 'Replacement case must begin at its reported revision';
                        END IF;
                    ELSE
                        IF ROW(NEW."Id", NEW."CustomerId", NEW."Reason", NEW."OriginalsJson", NEW."EvidenceJson", NEW."ReportedBy", NEW."ReportedAt")
                           IS DISTINCT FROM ROW(OLD."Id", OLD."CustomerId", OLD."Reason", OLD."OriginalsJson", OLD."EvidenceJson", OLD."ReportedBy", OLD."ReportedAt")
                           OR NEW."Revision" <> OLD."Revision" + 1
                           OR jsonb_typeof(NEW."CommandsJson") <> 'array'
                           OR jsonb_array_length(NEW."CommandsJson") <> jsonb_array_length(OLD."CommandsJson") + 1
                           OR NEW."CommandsJson" - (jsonb_array_length(NEW."CommandsJson") - 1) <> OLD."CommandsJson" THEN
                            RAISE EXCEPTION 'Replacement snapshots are immutable and commands must append one revision';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER replacement_case_append_only BEFORE INSERT OR UPDATE OR DELETE ON "ReplacementCase"
                    FOR EACH ROW EXECUTE FUNCTION replacement_case_append_only();

                CREATE FUNCTION replacement_owned_fact_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Replacement lineage and operation receipts are immutable';
                END;
                $$;
                CREATE TRIGGER replacement_lineage_immutable BEFORE UPDATE OR DELETE ON "ReplacementAffectedOrder"
                    FOR EACH ROW EXECUTE FUNCTION replacement_owned_fact_immutable();
                CREATE TRIGGER replacement_receipt_immutable BEFORE UPDATE OR DELETE ON "ReplacementOperation"
                    FOR EACH ROW EXECUTE FUNCTION replacement_owned_fact_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplacementAffectedOrder");

            migrationBuilder.DropTable(
                name: "ReplacementOperation");

            migrationBuilder.DropTable(
                name: "ReplacementCase");
            migrationBuilder.Sql("""
                DROP FUNCTION replacement_case_append_only();
                DROP FUNCTION replacement_owned_fact_immutable();
                """);
        }
    }
}
