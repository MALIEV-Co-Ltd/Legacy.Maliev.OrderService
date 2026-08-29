using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class FixTimestampColumnType : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Turnaround",
            table: "Order");

        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: true);

        migrationBuilder.AddColumn<int>(
            name: "Turnaround",
            table: "Order",
            type: "integer",
            nullable: true,
            computedColumnSql: "(\"FinishedDate\" - \"CreatedDate\"::date)",
            stored: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Turnaround",
            table: "Order");

        ConvertUtcTimestampColumns(migrationBuilder, toTimestampWithoutTimeZone: false);

        migrationBuilder.AddColumn<int>(
            name: "Turnaround",
            table: "Order",
            type: "integer",
            nullable: true,
            computedColumnSql: "(\"FinishedDate\" - (\"CreatedDate\" AT TIME ZONE 'UTC')::date)",
            stored: true);
    }

    private static void ConvertUtcTimestampColumns(MigrationBuilder migrationBuilder, bool toTimestampWithoutTimeZone)
    {
        var targetType = toTimestampWithoutTimeZone
            ? "timestamp without time zone"
            : "timestamp with time zone";
        var defaultSql = toTimestampWithoutTimeZone
            ? "CURRENT_TIMESTAMP AT TIME ZONE 'UTC'"
            : "CURRENT_TIMESTAMP";

        foreach (var (table, column) in UtcTimestampColumns)
        {
            migrationBuilder.Sql($"""
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" DROP DEFAULT;
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" TYPE {targetType}
                USING "{column}" AT TIME ZONE 'UTC';
                ALTER TABLE "{table}"
                ALTER COLUMN "{column}" SET DEFAULT {defaultSql};
                """);
        }
    }

    private static readonly (string Table, string Column)[] UtcTimestampColumns =
    [
        ("Process", "ModifiedDate"),
        ("Process", "CreatedDate"),
        ("OrderFile", "ModifiedDate"),
        ("OrderFile", "CreatedDate"),
        ("Order", "ModifiedDate"),
        ("Order", "CreatedDate"),
        ("FileFormat", "ModifiedDate"),
        ("FileFormat", "CreatedDate"),
        ("Category", "ModifiedDate"),
        ("Category", "CreatedDate")
    ];
}
