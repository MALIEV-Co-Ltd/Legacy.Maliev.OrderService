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
    public async Task NumericSearch_MatchesExactOrderIdAndNeverCustomerId_ForGeneralAndPendingLists()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var customerId = 424242;
        var order = await repository.CreateOrderAsync(
            Request(process.Id, false) with { CustomerId = customerId },
            default);

        Assert.Null(await repository.GetOrdersAsync(null, false, null, customerId.ToString(), 1, 50, default));
        Assert.Null(await repository.GetOrdersAsync(null, true, null, customerId.ToString(), 1, 50, default));
        Assert.Equal(
            [order.Id],
            (await repository.GetOrdersAsync(null, false, null, order.Id.ToString(), 1, 50, default))!.Items
                .Select(item => item.Id));
        Assert.Equal(
            [order.Id],
            (await repository.GetOrdersAsync(null, true, null, order.Id.ToString(), 1, 50, default))!.Items
                .Select(item => item.Id));
    }

    [Fact]
    public async Task GeneralSearch_CoversCommentAndTrackingNumber()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var token = Guid.NewGuid().ToString("N");
        var commentOrder = await repository.CreateOrderAsync(
            Request(process.Id) with { Comment = $"comment-{token}" },
            default);
        var trackingOrder = await repository.CreateOrderAsync(
            Request(process.Id) with { TrackingNumber = $"tracking-{token}" },
            default);

        var commentResult = await repository.GetOrdersAsync(null, false, null, $"comment-{token}", 1, 50, default);
        Assert.NotNull(commentResult);
        Assert.Equal([commentOrder.Id], commentResult.Items.Select(item => item.Id));
        Assert.Equal(
            [trackingOrder.Id],
            (await repository.GetOrdersAsync(null, false, null, $"tracking-{token}", 1, 50, default))!.Items
                .Select(item => item.Id));
    }

    [Fact]
    public async Task PendingSearch_OnlyCoversNameAndDescription()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var token = Guid.NewGuid().ToString("N");
        var nameOrder = await repository.CreateOrderAsync(
            Request(process.Id, false) with { Name = $"name-{token}" },
            default);
        var descriptionOrder = await repository.CreateOrderAsync(
            Request(process.Id, false) with { Description = $"description-{token}" },
            default);
        await repository.CreateOrderAsync(
            Request(process.Id, false) with
            {
                TrackingNumber = $"tracking-{token}",
                Comment = $"comment-{token}",
            },
            default);

        Assert.Equal(
            [nameOrder.Id],
            (await repository.GetOrdersAsync(null, true, null, $"name-{token}", 1, 50, default))!.Items
                .Select(item => item.Id));
        Assert.Equal(
            [descriptionOrder.Id],
            (await repository.GetOrdersAsync(null, true, null, $"description-{token}", 1, 50, default))!.Items
                .Select(item => item.Id));
        Assert.Null(await repository.GetOrdersAsync(null, true, null, $"tracking-{token}", 1, 50, default));
        Assert.Null(await repository.GetOrdersAsync(null, true, null, $"comment-{token}", 1, 50, default));
    }

    [Fact]
    public async Task Search_IsCaseInsensitiveAcrossEndpointFields()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var token = Guid.NewGuid().ToString("N");
        var general = await repository.CreateOrderAsync(
            Request(process.Id) with { Comment = $"MixedCase-{token}" },
            default);
        var pending = await repository.CreateOrderAsync(
            Request(process.Id, false) with { Description = $"PendingCase-{token}" },
            default);

        var generalResult = await repository.GetOrdersAsync(null, false, null, $"mixedcase-{token}", 1, 50, default);
        Assert.NotNull(generalResult);
        Assert.Equal([general.Id], generalResult.Items.Select(item => item.Id));
        Assert.Equal(
            [pending.Id],
            (await repository.GetOrdersAsync(null, true, null, $"pendingcase-{token}", 1, 50, default))!.Items
                .Select(item => item.Id));
    }

    [Fact]
    public async Task LegacyRemainingAndQuantitySortValues_PreserveOrderingAndStableTies()
    {
        await using var oc = OC();
        await using var sc = SC();
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var first = await repository.CreateOrderAsync(Request(process.Id) with { Quantity = 10, Manufactured = 8 }, default);
        var second = await repository.CreateOrderAsync(Request(process.Id) with { Quantity = 4, Manufactured = 2 }, default);
        var third = await repository.CreateOrderAsync(Request(process.Id) with { Quantity = 9, Manufactured = 1 }, default);

        Assert.Equal(
            [first.Id, second.Id, third.Id],
            await SortedIdsAsync(repository, (OrderSortType)8));
        Assert.Equal(
            [third.Id, first.Id, second.Id],
            await SortedIdsAsync(repository, (OrderSortType)9));
        Assert.Equal(
            [second.Id, third.Id, first.Id],
            await SortedIdsAsync(repository, (OrderSortType)10));
        Assert.Equal(
            [first.Id, third.Id, second.Id],
            await SortedIdsAsync(repository, (OrderSortType)11));
    }
    [Fact]
    public async Task CustomerOrderBoundary_EnforcesOwnershipAndIdempotentCancellation()
    {
        await using var oc = OC();
        await using var sc = SC(retryOnFailure: true);
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

    [Fact]
    public async Task AcceptedTransition_ConvergesCancellationAndRetryDoesNotDuplicateHistory()
    {
        await using var oc = OC();
        await using var sc = SC(retryOnFailure: true);
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var cache = Cache();
        var transitionTime = new DateTimeOffset(2026, 7, 18, 23, 30, 0, TimeSpan.Zero);
        var repository = Repo(oc, sc, cache, new FixedTimeProvider(transitionTime));
        var process = await CreateProcessAsync(repository);
        var order = await repository.CreateOrderAsync(
            Request(process.Id, false) with { LeadTime = 5, PromisedDate = null },
            default);
        var created = await repository.CreateStatusAsync(new("New", "New"), default);
        var accepted = await repository.CreateStatusAsync(new("accepted", "Accepted"), default);
        sc.Add(new OrderStatusTransition { OrderStatusId = created.Id, PossibleStatusId = accepted.Id });
        await sc.SaveChangesAsync();
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, created.Id, default));
        cache.Invocations.Clear();

        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, accepted.Id, default));
        oc.ChangeTracker.Clear();
        var acceptedOrder = await repository.GetOrderAsync(order.Id, default);
        Assert.False(acceptedOrder?.AllowCancellation);
        Assert.Equal(transitionTime.UtcDateTime.Date.AddDays(5), acceptedOrder?.PromisedDate);
        Assert.Equal(2, (await repository.GetHistoryAsync(order.Id, default)).Count);
        cache.Verify(value => value.RemoveAsync($"order:{order.Id}", It.IsAny<CancellationToken>()), Times.Once);

        await oc.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Order\" SET \"AllowCancellation\"=TRUE WHERE \"ID\"={order.Id}");
        oc.ChangeTracker.Clear();
        cache.Invocations.Clear();

        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, accepted.Id, default));
        oc.ChangeTracker.Clear();
        var retriedOrder = await repository.GetOrderAsync(order.Id, default);
        Assert.False(retriedOrder?.AllowCancellation);
        Assert.Equal(transitionTime.UtcDateTime.Date.AddDays(5), retriedOrder?.PromisedDate);
        Assert.Equal(2, (await repository.GetHistoryAsync(order.Id, default)).Count);
        cache.Verify(value => value.RemoveAsync($"order:{order.Id}", It.IsAny<CancellationToken>()), Times.Once);

        var existingPromisedDate = new DateTime(2026, 8, 15);
        var prePromisedOrder = await repository.CreateOrderAsync(
            Request(process.Id, false) with { LeadTime = 5, PromisedDate = existingPromisedDate },
            default);
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(prePromisedOrder.Id, created.Id, default));
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(prePromisedOrder.Id, accepted.Id, default));
        oc.ChangeTracker.Clear();
        Assert.Equal(existingPromisedDate, (await repository.GetOrderAsync(prePromisedOrder.Id, default))?.PromisedDate);

        var noLeadTimeOrder = await repository.CreateOrderAsync(
            Request(process.Id, false) with { LeadTime = null, PromisedDate = null },
            default);
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(noLeadTimeOrder.Id, created.Id, default));
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(noLeadTimeOrder.Id, accepted.Id, default));
        oc.ChangeTracker.Clear();
        Assert.Null((await repository.GetOrderAsync(noLeadTimeOrder.Id, default))?.PromisedDate);
    }

    [Fact]
    public async Task UnrelatedSameStatusTransition_PreservesInvalidTransitionContract()
    {
        await using var oc = OC();
        await using var sc = SC(retryOnFailure: true);
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var order = await repository.CreateOrderAsync(Request(process.Id, false), default);
        var created = await repository.CreateStatusAsync(new("New", "New"), default);

        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, created.Id, default));
        Assert.Equal(UpdateResult.InvalidTransition, await repository.TransitionAsync(order.Id, created.Id, default));
        Assert.Single(await repository.GetHistoryAsync(order.Id, default));
    }

    [Fact]
    public async Task AcceptedPartialFailure_IsRetryableAndCustomerCancellationFailsClosed()
    {
        await using var oc = OC();
        await using var sc = SC(retryOnFailure: true);
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var order = await repository.CreateOrderAsync(Request(process.Id, false), default);
        var created = await repository.CreateStatusAsync(new("New", "New"), default);
        var accepted = await repository.CreateStatusAsync(new("Accepted", "Accepted"), default);
        var cancelled = await repository.CreateStatusAsync(new("Cancelled", "Cancelled"), default);
        sc.AddRange(
            new OrderStatusTransition { OrderStatusId = created.Id, PossibleStatusId = accepted.Id },
            new OrderStatusTransition { OrderStatusId = accepted.Id, PossibleStatusId = cancelled.Id });
        await sc.SaveChangesAsync();
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, created.Id, default));
        await oc.Database.ExecuteSqlRawAsync(
            """
            CREATE OR REPLACE FUNCTION fail_cancellation_disable() RETURNS trigger AS $$
            BEGIN
                IF OLD."AllowCancellation" AND NOT NEW."AllowCancellation" THEN
                    RAISE EXCEPTION 'simulated order convergence failure';
                END IF;
                RETURN NEW;
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_cancellation_disable
            BEFORE UPDATE ON "Order"
            FOR EACH ROW EXECUTE FUNCTION fail_cancellation_disable();
            """);

        try
        {
            Assert.Equal(UpdateResult.Conflict, await repository.TransitionAsync(order.Id, accepted.Id, default));
            oc.ChangeTracker.Clear();
            Assert.True((await repository.GetOrderAsync(order.Id, default))?.AllowCancellation);
            Assert.Equal("Accepted", (await repository.GetLatestStatusAsync(order.Id, default))?.Name);
            Assert.Equal(UpdateResult.InvalidTransition, await repository.CancelCustomerOrderAsync(42, order.Id, default));
            Assert.Equal(2, (await repository.GetHistoryAsync(order.Id, default)).Count);
        }
        finally
        {
            await oc.Database.ExecuteSqlRawAsync(
                """
                DROP TRIGGER IF EXISTS fail_cancellation_disable ON "Order";
                DROP FUNCTION IF EXISTS fail_cancellation_disable();
                """);
        }

        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(order.Id, accepted.Id, default));
        oc.ChangeTracker.Clear();
        Assert.False((await repository.GetOrderAsync(order.Id, default))?.AllowCancellation);
        Assert.Equal(2, (await repository.GetHistoryAsync(order.Id, default)).Count);
    }

    [Fact]
    public async Task OrderUpdate_CannotReenableCancellationAfterAcceptedAndPreservesOtherStatuses()
    {
        await using var oc = OC();
        await using var sc = SC(retryOnFailure: true);
        await Task.WhenAll(oc.Database.MigrateAsync(), sc.Database.MigrateAsync());
        var repository = Repo(oc, sc);
        var process = await CreateProcessAsync(repository);
        var created = await repository.CreateStatusAsync(new("New", "New"), default);
        var accepted = await repository.CreateStatusAsync(new("accepted", "Accepted"), default);
        sc.Add(new OrderStatusTransition { OrderStatusId = created.Id, PossibleStatusId = accepted.Id });
        await sc.SaveChangesAsync();

        var acceptedOrder = await repository.CreateOrderAsync(Request(process.Id, false), default);
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(acceptedOrder.Id, created.Id, default));
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(acceptedOrder.Id, accepted.Id, default));
        Assert.Equal(
            UpdateResult.Updated,
            await repository.UpdateOrderAsync(acceptedOrder.Id, Request(process.Id, false) with { AllowCancellation = true }, null, default));
        oc.ChangeTracker.Clear();
        Assert.False((await repository.GetOrderAsync(acceptedOrder.Id, default))?.AllowCancellation);

        var newOrder = await repository.CreateOrderAsync(
            Request(process.Id, false) with { AllowCancellation = false },
            default);
        Assert.Equal(UpdateResult.Updated, await repository.TransitionAsync(newOrder.Id, created.Id, default));
        Assert.Equal(
            UpdateResult.Updated,
            await repository.UpdateOrderAsync(newOrder.Id, Request(process.Id, false) with { AllowCancellation = true }, null, default));
        oc.ChangeTracker.Clear();
        Assert.True((await repository.GetOrderAsync(newOrder.Id, default))?.AllowCancellation);
    }

    private static Mock<IOrderCache> Cache()
    {
        var cache = new Mock<IOrderCache>();
        cache.Setup(value => value.GetAsync<OrderResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrderResponse?)null);
        return cache;
    }

    private async Task<ProcessResponse> CreateProcessAsync(OrderRepository repository)
    {
        var category = await repository.CreateCategoryAsync(new("Accepted test"), default);
        return await repository.CreateProcessAsync(new(category.Id, "Accepted test"), default);
    }

    private static async Task<int[]> SortedIdsAsync(OrderRepository repository, OrderSortType sort) =>
        (await repository.GetOrdersAsync(null, false, sort, null, 1, 50, default))!.Items
            .Select(order => order.Id)
            .ToArray();

    private OrderRepository Repo(
        OrderDbContext o,
        OrderStatusDbContext s,
        Mock<IOrderCache>? cache = null,
        TimeProvider? clock = null) =>
        new(o, s, (cache ?? Cache()).Object, clock ?? TimeProvider.System);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
    private static UpsertOrderRequest Request(int p, bool finished = true) => new(42, null, "Part", "Part", p, null, null, null, 10, 3, 10m, 5m, 764, 5, DateTime.UtcNow.Date.AddDays(5), finished ? DateTime.UtcNow.Date.AddDays(3) : null, null, false, true, true, null); private OrderDbContext OC() => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(op.GetConnectionString()).Options); private OrderStatusDbContext SC(bool retryOnFailure = false) => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(sp.GetConnectionString(), options => { if (retryOnFailure) options.EnableRetryOnFailure(); }).Options);
}
