using System.Data.Common;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderDeletePartialFailureTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer orderDatabase = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer statusDatabase = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(orderDatabase.StartAsync(), statusDatabase.StartAsync());
        await using var orders = Orders();
        await using var statuses = Statuses();
        await Task.WhenAll(orders.Database.MigrateAsync(), statuses.Database.MigrateAsync());
    }

    public async Task DisposeAsync() => await Task.WhenAll(orderDatabase.DisposeAsync().AsTask(), statusDatabase.DisposeAsync().AsTask());

    [Fact]
    public async Task OrderDatabaseRejectsDelete_OrderAndFilesRollbackWithoutUntrackedHistoryLoss()
    {
        var id = await SeedAsync();
        await using var orders = Orders();
        await using var statuses = Statuses();
        var original = await statuses.History.AsNoTracking().SingleAsync(x => x.OrderId == id);
        await orders.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_order_delete() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'owned disposable order-delete fault';
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_order_delete BEFORE DELETE ON "Order"
            FOR EACH ROW EXECUTE FUNCTION reject_order_delete();
            """);
        try
        {
            await Assert.ThrowsAsync<OrderDeletionUnavailableException>(() => Repository(orders, statuses).DeleteOrderAsync(id, default));
            Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
            Assert.Single(await orders.Files.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
            var preserved = Assert.Single(await statuses.History.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
            Assert.Equal(original.Id, preserved.Id);
            Assert.Equal(original.OrderStatusId, preserved.OrderStatusId);
            Assert.Equal(original.CreatedDate, preserved.CreatedDate);
            Assert.Equal(original.ModifiedDate, preserved.ModifiedDate);
        }
        finally
        {
            await orders.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_order_delete ON \"Order\"; DROP FUNCTION reject_order_delete();");
        }
    }

    [Fact]
    public async Task CancellationBeforeOrderCommit_PropagatesAndPreservesBothGraphs()
    {
        var id = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var barrier = new CancelBeforeOrderCommit(cancellation);
        await using var orders = Orders(barrier);
        await using var statuses = Statuses();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repository(orders, statuses).DeleteOrderAsync(id, cancellation.Token));
        Assert.True(barrier.ObservedUncommittedFileDelete);
        await using var freshOrders = Orders();
        await using var freshStatuses = Statuses();
        Assert.True(await freshOrders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Single(await freshOrders.Files.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
        Assert.Single(await freshStatuses.History.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
    }

    [Fact]
    public async Task CancellationAfterOrderCommit_LeavesPendingIntentForFreshRetry()
    {
        var id = await SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var barrier = new CancelAfterOrderCommit(cancellation);
        await using var orders = Orders(barrier);
        await using var statuses = Statuses();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repository(orders, statuses).DeleteOrderAsync(id, cancellation.Token));
        Assert.True(barrier.ObservedDurableOrderCommit);
        await using var freshOrders = Orders();
        await using var freshStatuses = Statuses();
        Assert.False(await freshOrders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Empty(await freshOrders.Files.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
        var receipt = Assert.Single(await freshOrders.DeletionIntents.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        Assert.Null(receipt.CompletedAtUtc);
        Assert.Single(await freshStatuses.History.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
        Assert.True(await Repository(freshOrders, freshStatuses).DeleteOrderAsync(id, default));
        Assert.NotNull((await freshOrders.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
        Assert.Empty(await freshStatuses.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task SuccessfulDelete_RemovesBothGraphsAndMissingOrderReplayReturnsFalse()
    {
        var id = await SeedAsync();
        await using var orders = Orders();
        await using var statuses = Statuses();
        var repository = Repository(orders, statuses);
        Assert.True(await repository.DeleteOrderAsync(id, default));
        Assert.False(await repository.DeleteOrderAsync(id, default));
        Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Empty(await orders.Files.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
        Assert.Empty(await statuses.History.AsNoTracking().Where(x => x.OrderId == id).ToListAsync());
    }

    private async Task<int> SeedAsync()
    {
        await using var orders = Orders();
        await using var statuses = Statuses();
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var category = new Category { Name = "Owned delete fixture" };
        var process = new Process { Name = "Owned delete fixture", Category = category };
        var order = new Order { Name = "Owned delete fixture", Process = process, Quantity = 1, CreatedDate = now, ModifiedDate = now };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "part.stl", CreatedDate = now, ModifiedDate = now });
        await orders.SaveChangesAsync();
        var status = new OrderStatus { Name = "New", CreatedDate = now, ModifiedDate = now };
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = status, CreatedDate = now, ModifiedDate = now });
        await statuses.SaveChangesAsync();
        return order.Id;
    }

    private OrderDbContext Orders(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(Connection(orderDatabase)).AddInterceptors(interceptors).Options);
    private OrderStatusDbContext Statuses(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(Connection(statusDatabase)).AddInterceptors(interceptors).Options);
    private static string Connection(PostgreSqlContainer container) => new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Pooling = false }.ConnectionString;
    private static OrderRepository Repository(OrderDbContext orders, OrderStatusDbContext statuses) => new(orders, statuses, Mock.Of<IOrderCache>(), TimeProvider.System);

    // The immutable archived test marks the OLD history-first autocommit boundary.
    // This active prerequisite is explicitly split into pre-Order-commit preservation and post-commit recovery.
    private sealed class CancelBeforeOrderCommit(CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        public bool ObservedUncommittedFileDelete { get; private set; }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM \"OrderFile\"", StringComparison.Ordinal))
            {
                ObservedUncommittedFileDelete = true;
                cancellation.Cancel();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CancelAfterOrderCommit(CancellationTokenSource cancellation) : DbTransactionInterceptor
    {
        public bool ObservedDurableOrderCommit { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            ObservedDurableOrderCommit = true;
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }
}
