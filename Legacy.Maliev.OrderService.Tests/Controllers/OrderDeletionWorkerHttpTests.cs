using System.Net;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

// Normal Production hosts and real disposable stores. Only owned fixture configuration enables workers.
public sealed class OrderDeletionWorkerHttpTests(OrderDeletionReadinessFixture fixture) : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledWorker_RecoversExactPendingReceipt_WithConcurrentHostOwnership(bool secondHost)
    {
        await using var first = await fixture.AppAsync(null, workerEnabled: true);
        await using var scope = first.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var receipt = await PendingAsync(orders, statuses);
        await using var second = secondHost ? fixture.AdditionalWorkerApp(first) : null;
        using var firstClient = first.CreateClient();
        using var secondClient = second?.CreateClient();
        using var healthy = await firstClient.GetAsync("/order/readiness");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        var limit = DateTime.UtcNow.AddSeconds(6);
        bool complete;
        do
        {
            complete = await orders.DeletionIntents.AsNoTracking().AnyAsync(x => x.OrderId == receipt.OrderId && x.CompletedAtUtc != null);
            if (!complete) await Task.Delay(50);
        } while (!complete && DateTime.UtcNow < limit);
        Assert.True(complete, "Enabled normal host did not recover durable due cleanup intent.");
        var stored = await orders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.DeletionId, stored.DeletionId);
        Assert.NotNull(stored.StatusCleanupCompletedAtUtc);
        Assert.Empty(await statuses.History.AsNoTracking().ToListAsync());
        Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == receipt.OrderId));
        Assert.Equal(1, await statuses.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM owned_cleanup_probe").SingleAsync());
    }

    [Fact]
    public async Task DisabledWorker_DoesNotMutatePendingIntentOrHistory()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var receipt = await PendingAsync(orders, statuses);
        using var client = app.CreateClient();
        using var healthy = await client.GetAsync("/order/readiness");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        await Task.Delay(1500);
        var stored = await orders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.DeletionId, stored.DeletionId);
        Assert.Null(stored.CompletedAtUtc);
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        Assert.Equal(0, await statuses.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM owned_cleanup_probe").SingleAsync());
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("poll")]
    [InlineData("timeout")]
    [InlineData("backoff")]
    public async Task InvalidEnabledWorkerOptions_DoNotAdmitCleanup(string invalid)
    {
        await using var app = await fixture.AppAsync(null, workerEnabled: true, configure: services => services.PostConfigure<OrderDeletionWorkerOptions>(options =>
        {
            if (invalid == "batch") options.BatchSize = 0;
            if (invalid == "poll") options.PollIntervalSeconds = 0;
            if (invalid == "timeout") options.AttemptTimeoutSeconds = 0;
            if (invalid == "backoff") options.MaxBackoffSeconds = 301;
        }));
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var receipt = await PendingAsync(orders, statuses);
        await Task.Delay(1500);
        Assert.Null((await orders.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
        Assert.Equal(receipt.DeletionId, (await orders.DeletionIntents.AsNoTracking().SingleAsync()).DeletionId);
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        Assert.Equal(0, await statuses.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM owned_cleanup_probe").SingleAsync());
    }

    [Fact]
    public async Task NormalWorker_DeadlineAndShutdownCancelStatusAttempt_LeavingDurablePendingAuthority()
    {
        await using var app = await fixture.AppAsync(null, workerEnabled: true, configure: services => services.PostConfigure<OrderDeletionWorkerOptions>(options => options.AttemptTimeoutSeconds = 1));
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var receipt = await PendingAsync(orders, statuses);
        var orderConnection = orders.Database.GetConnectionString();
        var statusConnection = statuses.Database.GetConnectionString();
        await statuses.Database.ExecuteSqlRawAsync("CREATE FUNCTION delay_owned_worker_cleanup() RETURNS trigger AS $$ BEGIN PERFORM pg_sleep(60); RETURN OLD; END; $$ LANGUAGE plpgsql; CREATE TRIGGER delay_owned_worker_cleanup BEFORE DELETE ON \"OrderStatusHistory\" FOR EACH ROW EXECUTE FUNCTION delay_owned_worker_cleanup();");
        await using var observer = new NpgsqlConnection(statusConnection);
        await observer.OpenAsync();
        var limit = DateTime.UtcNow.AddSeconds(6);
        var observed = false;
        while (!observed && DateTime.UtcNow < limit)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep' AND query LIKE '%OrderStatusHistory%')", observer);
            observed = (bool)(await command.ExecuteScalarAsync())!;
            if (!observed) await Task.Delay(20);
        }
        Assert.True(observed, "Actual configured worker never reached the owned cancellable Status command.");
        await Task.Delay(1500);
        await scope.DisposeAsync();
        await app.DisposeAsync();
        await using var freshOrders = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(orderConnection).Options);
        await using var freshStatuses = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(statusConnection).Options);
        var pending = await freshOrders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.DeletionId, pending.DeletionId);
        Assert.Null(pending.CompletedAtUtc);
        Assert.Null(pending.StatusCleanupCompletedAtUtc);
        Assert.Single(await freshStatuses.History.AsNoTracking().ToListAsync());
        Assert.Equal(0, await freshStatuses.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM owned_cleanup_probe").SingleAsync());
    }

    private static async Task<OrderDeletionIntent> PendingAsync(OrderDbContext orders, OrderStatusDbContext statuses)
    {
        await statuses.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE owned_cleanup_probe (event integer NOT NULL);
            CREATE FUNCTION observe_owned_cleanup() RETURNS trigger AS $$
            BEGIN INSERT INTO owned_cleanup_probe VALUES (1); RETURN NULL; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER observe_owned_cleanup AFTER DELETE ON "OrderStatusHistory"
            FOR EACH STATEMENT EXECUTE FUNCTION observe_owned_cleanup();
            """);
        var order = new Order { Name = "Owned worker fixture", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        var receipt = new OrderDeletionIntent { OrderId = order.Id, DeletionId = Guid.NewGuid(), RequestedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow };
        await orders.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var attempt = new OrderDbContext((DbContextOptions<OrderDbContext>)orders.GetService<IDbContextOptions>());
            await using var transaction = await attempt.Database.BeginTransactionAsync();
            attempt.DeletionIntents.Add(receipt);
            await attempt.SaveChangesAsync();
            await attempt.Orders.Where(x => x.Id == order.Id).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        });
        return receipt;
    }
}
