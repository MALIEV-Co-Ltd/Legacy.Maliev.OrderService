using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderDeletionSchemaTests(OrderDeletionSchemaFixture fixture) : IClassFixture<OrderDeletionSchemaFixture>
{
    private const string PreviousMigration = "20260829182133_AddDurableOrderOperationKey";

    [Fact]
    public async Task PhysicalReadiness_CallerCancellationPropagates()
    {
        await using var db = await fixture.DatabaseAsync();
        await db.Database.MigrateAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OrderDeletionSchemaReadiness.CheckAsync(db, cancellation.Token));
    }

    [Fact]
    public async Task AdditiveSnapshot_HasNoUnrepresentedModelChanges()
    {
        await using var db = await fixture.DatabaseAsync();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task AdditiveMigration_PreservesExistingOrderComputedValuesAndCreateReceiptOnRerun()
    {
        await using var db = await fixture.DatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        var created = new DateTime(2026, 1, 1);
        var order = new Order
        {
            Name = "Owned additive fixture",
            OperationKey = "owned-create-receipt",
            Quantity = 10,
            Manufactured = 4,
            UnitPrice = 20,
            DiscountPercent = 10,
            CreatedDate = created,
            ModifiedDate = created,
            Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } }
        };
        db.Add(order);
        await db.SaveChangesAsync();
        var id = order.Id;
        await db.Database.MigrateAsync();
        await db.Database.MigrateAsync();
        db.ChangeTracker.Clear();
        var preserved = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal("owned-create-receipt", preserved.OperationKey);
        Assert.Equal(created, preserved.CreatedDate);
        Assert.Equal(created, preserved.ModifiedDate);
        Assert.Equal(6, preserved.Remaining);
        Assert.Equal(180, preserved.Subtotal);
        Assert.True(await IntentTableExistsAsync(db), "Additive deletion-intent table must physically exist after migration.");
    }

    [Fact]
    public async Task DurableReceipt_IsUniqueAndQueueIndexSupportsPendingDueWork()
    {
        await using var db = await fixture.DatabaseAsync();
        await db.Database.MigrateAsync();
        Assert.True(await IntentTableExistsAsync(db), "Durable deletion receipt is absent.");
        var receipt = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"OrderDeletionIntent\" (\"OrderId\", \"DeletionId\", \"RequestedAtUtc\", \"AttemptCount\", \"NextAttemptAtUtc\") VALUES (1, {receipt}, {now}, 0, {now})");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"OrderDeletionIntent\" (\"OrderId\", \"DeletionId\", \"RequestedAtUtc\", \"AttemptCount\", \"NextAttemptAtUtc\") VALUES (2, {receipt}, {now}, 0, {now})"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"OrderDeletionIntent\" SET \"AttemptCount\" = -1 WHERE \"OrderId\" = 1"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'OrderDeletionIntent'", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var indexes = new Dictionary<string, string>();
        while (await reader.ReadAsync()) indexes.Add(reader.GetString(0), reader.GetString(1));
        Assert.Contains("UNIQUE", indexes["IX_OrderDeletionIntent_DeletionId"], StringComparison.Ordinal);
        Assert.Contains("(\"NextAttemptAtUtc\", \"OrderId\")", indexes["IX_OrderDeletionIntent_PendingDue"], StringComparison.Ordinal);
        Assert.Contains("WHERE (\"CompletedAtUtc\" IS NULL)", indexes["IX_OrderDeletionIntent_PendingDue"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviousSchema_HasNoDeletionAuthorityTableAndRejectsReceiptQuery()
    {
        await using var db = await fixture.DatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.False(await IntentTableExistsAsync(db));
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT \"DeletionId\" FROM \"OrderDeletionIntent\" LIMIT 1"));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
    }

    private static async Task<bool> IntentTableExistsAsync(OrderDbContext db)
    {
        await using var connection = new NpgsqlConnection(db.Database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"OrderDeletionIntent\"') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}

public sealed class OrderDeletionSchemaFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
    public async Task<OrderDbContext> DatabaseAsync()
    {
        var name = "owned_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        var target = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
        return new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(target).Options);
    }
}
