using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class TimestampMigrationContractTests
{
    [Theory]
    [InlineData(
        "Legacy.Maliev.OrderService.Data/Migrations/Order/20260721030103_FixTimestampColumnType.cs",
        "Process", "ModifiedDate", "Process", "CreatedDate", "OrderFile", "ModifiedDate", "OrderFile", "CreatedDate", "Order", "ModifiedDate", "Order", "CreatedDate", "FileFormat", "ModifiedDate", "FileFormat", "CreatedDate", "Category", "ModifiedDate", "Category", "CreatedDate")]
    [InlineData(
        "Legacy.Maliev.OrderService.Data/Migrations/OrderStatus/20260721030107_FixTimestampColumnType.cs",
        "OrderStatusHistory", "ModifiedDate", "OrderStatusHistory", "CreatedDate", "OrderStatusHasPossibleStatus", "ModifiedDate", "OrderStatusHasPossibleStatus", "CreatedDate", "OrderStatus", "ModifiedDate", "OrderStatus", "CreatedDate")]
    public void TimestampMigrations_UseExplicitUtcConversions(string relativePath, params string[] tableColumns)
    {
        var source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("migrationBuilder.AlterColumn<DateTime>", source, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(source, "toTimestampWithoutTimeZone:").Count);
        Assert.Contains("ALTER COLUMN \"{column}\" DROP DEFAULT;", source, StringComparison.Ordinal);
        Assert.Contains("USING \"{column}\" AT TIME ZONE 'UTC'", source, StringComparison.Ordinal);

        for (var index = 0; index < tableColumns.Length; index += 2)
        {
            Assert.Contains($"(\"{tableColumns[index]}\", \"{tableColumns[index + 1]}\")", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void OrderTimestampMigration_RecreatesOnlyTheComputedTurnaroundColumn()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "Legacy.Maliev.OrderService.Data/Migrations/Order/20260721030103_FixTimestampColumnType.cs"));

        Assert.Equal(2, Regex.Matches(source, "migrationBuilder.DropColumn\\(").Count);
        Assert.Equal(4, Regex.Matches(source, "name: \"Turnaround\"[,\\r\\n]").Count);
        Assert.Equal(2, Regex.Matches(source, "computedColumnSql:").Count);
        Assert.DoesNotContain("name: \"CreatedDate\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("name: \"ModifiedDate\"", source, StringComparison.Ordinal);
        Assert.Contains("computedColumnSql: \"(\\\"FinishedDate\\\" - \\\"CreatedDate\\\"::date)\"", source, StringComparison.Ordinal);
        Assert.Contains("computedColumnSql: \"(\\\"FinishedDate\\\" - (\\\"CreatedDate\\\" AT TIME ZONE 'UTC')::date)\"", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { new DirectoryInfo(Path.GetDirectoryName(sourceFile)!), new DirectoryInfo(Directory.GetCurrentDirectory()), new DirectoryInfo(AppContext.BaseDirectory) })
        {
            for (var directory = start; directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException($"Could not find migration source '{relativePath}'.");
    }
}
