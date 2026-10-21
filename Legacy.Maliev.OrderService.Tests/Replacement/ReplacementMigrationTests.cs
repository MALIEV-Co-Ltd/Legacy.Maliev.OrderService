using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Data.Replacement;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

public sealed class ReplacementMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public async Task DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task Canonical_forward_migrations_include_replacement_without_altering_legacy_columns()
    {
        await using var db = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        await db.Database.MigrateAsync();
        Assert.NotNull(db.Model.FindEntityType(typeof(ReplacementCaseRow)));
        Assert.Equal(0, await db.Set<ReplacementCaseRow>().CountAsync());
        var remaining = db.Model.FindEntityType(typeof(Legacy.Maliev.OrderService.Domain.Order))!
            .FindProperty("Remaining")!;
        Assert.Equal("\"Quantity\" - \"Manufactured\"", remaining.GetComputedColumnSql());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Original_with_replacement_denies_deletion_without_losing_history_or_creating_tombstone()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync();
        var category = new Category { Name = "Manufacturing" };
        var process = new Process { Name = "Mold", Category = category };
        var order = new Order { CustomerId = 42, Process = process, Quantity = 1, Manufactured = 1, TrackingNumber = "ORIGINAL" };
        db.Add(order); await db.SaveChangesAsync();
        var store = new ReplacementStore(() => new OrderDbContext(options));
        await store.CreateAsync(42, ReplacementReason.CarrierDamage, [new(order.Id, 1)], new(Guid.NewGuid(), Guid.NewGuid()), 7, Guid.NewGuid(), DateTimeOffset.UtcNow, default);
        // Status must never be touched when original-order deletion is denied.
        await using var statuses = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=unused;Timeout=1").Options);
        var repository = new OrderRepository(db, statuses, new MemoryCache(), TimeProvider.System);
        Assert.Equal(OrderDeletionResult.Conflict, await repository.DeleteOrderWithRecoveryAsync(order.Id, default));
        Assert.True(await db.Orders.AsNoTracking().AnyAsync(x => x.Id == order.Id));
        Assert.Empty(await db.DeletionIntents.ToListAsync());
        Assert.Single(await db.Set<ReplacementCaseRow>().ToListAsync());
    }

    [Fact]
    public async Task Database_denies_original_snapshot_and_audit_or_operation_receipt_rewrites()
    {
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderDbContext(options);
        await db.Database.MigrateAsync();
        var order = new Order
        {
            CustomerId = 42,
            Quantity = 1,
            Manufactured = 1,
            Process = new Process { Name = "Mold", Category = new Category { Name = "Manufacturing" } }
        };
        db.Add(order); await db.SaveChangesAsync();
        var store = new ReplacementStore(() => new OrderDbContext(options));
        var value = await store.CreateAsync(42, ReplacementReason.CarrierDamage, [new(order.Id, 1)],
            new(Guid.NewGuid(), Guid.NewGuid()), 7, Guid.NewGuid(), DateTimeOffset.UtcNow, default);
        await store.ExecuteAsync(value.Id, 1, Guid.NewGuid(), 8,
            new ApproveReplacement(ReturnDecision.Waived, false, false, "Approved"), DateTimeOffset.UtcNow, default);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReplacementCase\" SET \"OriginalsJson\" = '[]'::jsonb WHERE \"Id\" = {value.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReplacementCase\" SET \"CommandsJson\" = '[]'::jsonb, \"Revision\" = 1 WHERE \"Id\" = {value.Id}"));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ReplacementOperation\" SET \"ResultRevision\" = 99 WHERE \"CaseId\" = {value.Id}"));
        Assert.Equal(2, (await store.GetAsync(value.Id, default))!.Value.Revision);
    }

    private sealed class MemoryCache : IOrderCache
    {
        public Task<T?> GetAsync<T>(string k, CancellationToken c) where T : class => Task.FromResult<T?>(null);
        public Task SetAsync<T>(string k, T v, TimeSpan t, CancellationToken c) where T : class => Task.CompletedTask;
        public Task RemoveAsync(string k, CancellationToken c) => throw new InvalidOperationException("Denied deletion must not clear cache.");
    }
}
