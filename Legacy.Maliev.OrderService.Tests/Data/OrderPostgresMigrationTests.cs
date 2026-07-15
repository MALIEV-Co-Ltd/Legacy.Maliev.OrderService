using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;
using Testcontainers.PostgreSql;
namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer op = new PostgreSqlBuilder("postgres:18-alpine").Build(), sp = new PostgreSqlBuilder("postgres:18-alpine").Build(); public Task InitializeAsync() => Task.WhenAll(op.StartAsync(), sp.StartAsync()); public async Task DisposeAsync() { await op.DisposeAsync(); await sp.DisposeAsync(); }
    [Fact] public async Task Migrations_PreserveComputedOrderCatalogFilesAndTransitionGraph() { await using var oc = OC(); await using var sc = SC(); await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync()); var r = Repo(oc, sc); var cat = await r.CreateCategoryAsync(new("Additive"), default); var proc = await r.CreateProcessAsync(new(cat.Id, "FDM"), default); var order = await r.CreateOrderAsync(Request(proc.Id), default); var file = await r.CreateFileAsync(order.Id, "legacy-orders", "orders/1.stl", default); var n = await r.CreateStatusAsync(new("New", "New"), default); var rev = await r.CreateStatusAsync(new("Reviewing", "Reviewing"), default); var shipped = await r.CreateStatusAsync(new("Shipped", "Shipped"), default); sc.Add(new OrderStatusTransition { OrderStatusId = n.Id, PossibleStatusId = rev.Id }); await sc.SaveChangesAsync(); Assert.Equal(UpdateResult.Updated, await r.TransitionAsync(order.Id, n.Id, default)); Assert.Equal(UpdateResult.InvalidTransition, await r.TransitionAsync(order.Id, shipped.Id, default)); Assert.Equal(UpdateResult.Updated, await r.TransitionAsync(order.Id, rev.Id, default)); oc.ChangeTracker.Clear(); var loaded = await r.GetOrderAsync(order.Id, default); Assert.Equal(7, loaded?.Remaining); Assert.Equal(95m, loaded?.Subtotal); Assert.Equal(3, loaded?.Turnaround); Assert.Equal("orders/1.stl", file?.ObjectName); Assert.Equal(2, (await r.GetHistoryAsync(order.Id, default)).Count); Assert.Single(await r.GetAvailableStatusesAsync(n.Id, default)); Assert.Equal(5, await oc.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Order','OrderFile','Process','Category','FileFormat')").SingleAsync()); Assert.Equal(3, await sc.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('OrderStatus','OrderStatusHasPossibleStatus','OrderStatusHistory')").SingleAsync()); }
    [Fact] public async Task PendingCustomerAndConcurrencyBoundaries_WorkOnPostgres18() { await using var oc = OC(); await using var sc = SC(); await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync()); var r = Repo(oc, sc); var cat = await r.CreateCategoryAsync(new("Machining"), default); var proc = await r.CreateProcessAsync(new(cat.Id, "CNC"), default); var order = await r.CreateOrderAsync(Request(proc.Id, false), default); Assert.Single((await r.GetOrdersAsync(42, true, null, null, 1, 50, default))!.Items); var stale = order.ModifiedDate!.Value; await oc.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Order\" SET \"ModifiedDate\"={stale.AddMinutes(1)} WHERE \"ID\"={order.Id}"); oc.ChangeTracker.Clear(); Assert.Equal(UpdateResult.Conflict, await r.UpdateOrderAsync(order.Id, Request(proc.Id, false), new DateTimeOffset(stale), default)); }
    [Fact]
    public async Task CustomerOrderBoundary_EnforcesOwnershipAndIdempotentCancellation()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var category = await repository.CreateCategoryAsync(new("Machining"), default);
        var process = await repository.CreateProcessAsync(new(category.Id, "CNC"), default);
        var order = await repository.CreateOrderAsync(Request(process.Id, false), default);
        await repository.CreateFileAsync(order.Id, "legacy-orders", "orders/customer-part.step", default);
        var created = await repository.CreateStatusAsync(new("New", "New"), default);
        var cancelled = await repository.CreateStatusAsync(new("Cancelled", "Cancelled"), default);
        sc.Add(new OrderStatusTransition { OrderStatusId = created.Id, PossibleStatusId = cancelled.Id });
        await sc.SaveChangesAsync();
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, created.Id, default));

        Assert.Null(await repository.GetCustomerOrderAsync(41, order.Id, default));
        var details = await repository.GetCustomerOrderAsync(42, order.Id, default);
        Assert.NotNull(details);
        Assert.Equal("CNC", details.Process?.Name);
        Assert.Single(details.Files);
        Assert.Single(details.History);
        Assert.Equal(UpdateResult.NotFound, await repository.CancelCustomerOrderAsync(41, order.Id, default));

        Assert.Equal(UpdateResult.Updated, await repository.CancelCustomerOrderAsync(42, order.Id, default));
        Assert.Equal(UpdateResult.Updated, await repository.CancelCustomerOrderAsync(42, order.Id, default));
        oc.ChangeTracker.Clear();
        Assert.False((await repository.GetOrderAsync(order.Id, default))?.AllowCancellation);
        Assert.Equal("Cancelled", (await repository.GetLatestStatusAsync(order.Id, default))?.Name);
        Assert.Equal(2, (await repository.GetHistoryAsync(order.Id, default)).Count);
    }
    private OrderRepository Repo(OrderDbContext o, OrderStatusDbContext s) { var c = new Mock<IOrderCache>(); c.Setup(x => x.GetAsync<OrderResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((OrderResponse?)null); return new(o, s, c.Object, TimeProvider.System); }
    private static UpsertOrderRequest Request(int p, bool finished = true) => new(42, null, "Part", "Part", p, null, null, null, 10, 3, 10m, 5m, 764, 5, DateTime.UtcNow.Date.AddDays(5), finished ? DateTime.UtcNow.Date.AddDays(3) : null, null, false, true, true, null); private OrderDbContext OC() => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(op.GetConnectionString()).Options); private OrderStatusDbContext SC() => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(sp.GetConnectionString()).Options);
}
