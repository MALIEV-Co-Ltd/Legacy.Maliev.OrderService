using System.Data.Common;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderDeletionRecoveryTests(OrderDeletionSchemaFixture fixture) : IClassFixture<OrderDeletionSchemaFixture>
{
    [Fact]
    public async Task SuccessfulDeletion_PersistsCompletedReceiptAndMissingReplayDoesNotReplaceIt()
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        var repository = Repository(orders, statuses);
        Assert.True(await repository.DeleteOrderAsync(seed.OrderId, default));
        var receipt = Assert.Single(await orders.DeletionIntents.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        Assert.NotNull(receipt.StatusCleanupCompletedAtUtc);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.False(await repository.DeleteOrderAsync(seed.OrderId, default));
        Assert.Equal(receipt.DeletionId, (await orders.DeletionIntents.AsNoTracking().SingleAsync()).DeletionId);
    }

    [Fact]
    public async Task StatusCleanupFailure_LeavesDurablePendingIntentAndRetryCompletes()
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        await statuses.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_owned_history_cleanup() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'owned disposable status cleanup fault'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_owned_history_cleanup BEFORE DELETE ON "OrderStatusHistory"
            FOR EACH ROW EXECUTE FUNCTION reject_owned_history_cleanup();
            """);
        try
        {
            await Record.ExceptionAsync(() => Repository(orders, statuses).DeleteOrderAsync(seed.OrderId, default));
            Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == seed.OrderId));
            Assert.Empty(await orders.Files.AsNoTracking().ToListAsync());
            var pending = Assert.Single(await orders.DeletionIntents.AsNoTracking().ToListAsync());
            Assert.Null(pending.CompletedAtUtc);
            Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        }
        finally
        {
            await statuses.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_owned_history_cleanup ON \"OrderStatusHistory\"; DROP FUNCTION reject_owned_history_cleanup();");
        }
        Assert.True(await Repository(orders, statuses).DeleteOrderAsync(seed.OrderId, default));
        Assert.NotNull((await orders.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
        Assert.Empty(await statuses.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LostOrderCommitAcknowledgement_ReconcilesExactDurableReceipt()
    {
        var seed = await SeedAsync();
        var lost = new LoseCommitAcknowledgement();
        await using var orders = Orders(seed, lost);
        await using var statuses = Statuses(seed);
        var completed = false;
        var failure = await Record.ExceptionAsync(async () => completed = await Repository(orders, statuses).DeleteOrderAsync(seed.OrderId, default));
        Assert.True(lost.ObservedDurableCommit);
        Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == seed.OrderId));
        var receipt = Assert.Single(await orders.DeletionIntents.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        Assert.Null(failure);
        Assert.True(completed);
        Assert.True(await Repository(orders, statuses).DeleteOrderAsync(seed.OrderId, default) || receipt.CompletedAtUtc is not null);
        var replay = await orders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(receipt.DeletionId, replay.DeletionId);
        Assert.NotNull(replay.CompletedAtUtc);
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("history-put")]
    [InlineData("history-delete")]
    [InlineData("order-delete")]
    [InlineData("order-put")]
    [InlineData("file-create")]
    [InlineData("file-put")]
    [InlineData("file-delete")]
    public async Task OrderLifetimeFence_PreventsWriterPassingHeldOrderLock(string operation)
    {
        var seed = await SeedAsync();
        await using var holder = Orders(seed);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var identity = $"legacy-order-lifetime\n{seed.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
        await using var writerOrders = Orders(seed);
        await using var writerStatuses = Statuses(seed);
        var repository = Repository(writerOrders, writerStatuses);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task action = operation switch
        {
            "transition" => repository.TransitionAsync(seed.OrderId, seed.TargetId, deadline.Token),
            "history-put" => repository.UpdateHistoryAsync(seed.HistoryId, new UpsertOrderStatusHistoryRequest(seed.OrderId, seed.TargetId), null, deadline.Token),
            "history-delete" => repository.DeleteHistoryAsync(seed.HistoryId, deadline.Token),
            "order-put" => repository.UpdateOrderAsync(seed.OrderId, new UpsertOrderRequest(null, null, "Owned update", null, seed.ProcessId, null, null, null, 2, 0, null, null, null, null, null, null, null, false, false, false, null), null, deadline.Token),
            "file-create" => repository.CreateFileAsync(seed.OrderId, "owned-fixture", "new.stl", deadline.Token),
            "file-put" => repository.UpdateFileAsync(seed.FileId, new UpsertOrderFileRequest(seed.OrderId, "owned-fixture", "updated.stl"), deadline.Token),
            "file-delete" => repository.DeleteFileAsync(seed.FileId, deadline.Token),
            _ => repository.DeleteOrderAsync(seed.OrderId, deadline.Token)
        };
        try
        {
            await using var observer = new NpgsqlConnection(seed.OrderConnection);
            await observer.OpenAsync(deadline.Token);
            var blocked = false;
            while (!action.IsCompleted && !blocked)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory')", observer);
                blocked = (bool)(await command.ExecuteScalarAsync(deadline.Token))!;
                if (!blocked) await Task.Delay(20, deadline.Token);
            }
            Assert.True(blocked, "Writer completed before acquiring the held authoritative Order lifetime fence.");
            Assert.False(action.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync();
            await action;
        }
    }

    [Theory]
    [InlineData("put")]
    [InlineData("delete")]
    [InlineData("history")]
    [InlineData("latest")]
    public async Task PendingValidatedIntent_DeniesHistoryMutationAndHidesOnlyItsHistory(string operation)
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        await TombstoneAsync(orders, seed.OrderId);
        var repository = Repository(orders, statuses);
        switch (operation)
        {
            case "put": Assert.Equal(UpdateResult.NotFound, await repository.UpdateHistoryAsync(seed.HistoryId, new UpsertOrderStatusHistoryRequest(seed.OrderId, seed.TargetId), null, default)); break;
            case "delete": Assert.False(await repository.DeleteHistoryAsync(seed.HistoryId, default)); break;
            case "history": Assert.Empty(await repository.GetHistoryAsync(seed.OrderId, default)); break;
            default: Assert.Null(await repository.GetLatestStatusAsync(seed.OrderId, default)); break;
        }
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("read")]
    [InlineData("list")]
    public async Task LiveOrderWithIntent_IsConflictNotDeletionOrHiddenImport(string operation)
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = seed.OrderId, DeletionId = Guid.NewGuid(), RequestedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow });
        await orders.SaveChangesAsync();
        var repository = Repository(orders, statuses);
        var failure = await Record.ExceptionAsync(async () =>
        {
            if (operation == "delete") await repository.DeleteOrderAsync(seed.OrderId, default);
            else if (operation == "read") await repository.GetOrderAsync(seed.OrderId, default);
            else await repository.GetOrdersAsync(null, false, null, null, 1, 50, default);
        });
        Assert.NotNull(failure);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == seed.OrderId));
        Assert.Single(await orders.Files.AsNoTracking().ToListAsync());
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PendingValidatedIntent_DeniesStaleCachedOrderAndLists()
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        var cache = new OwnedMemoryCache();
        var repository = new OrderRepository(orders, statuses, cache, TimeProvider.System);
        Assert.NotNull(await repository.GetOrderAsync(seed.OrderId, default));
        await TombstoneAsync(orders, seed.OrderId);
        Assert.Null(await repository.GetOrderAsync(seed.OrderId, default));
        Assert.Null(await repository.GetOrdersAsync(null, false, null, null, 1, 50, default));
    }

    [Fact]
    public async Task HistoricalOrphanWithoutIntent_RetainsExistingHistoryReadBehavior()
    {
        var seed = await SeedAsync();
        await using var orders = Orders(seed);
        await using var statuses = Statuses(seed);
        await orders.Files.ExecuteDeleteAsync();
        await orders.Orders.ExecuteDeleteAsync();
        Assert.Single(await Repository(orders, statuses).GetHistoryAsync(seed.OrderId, default));
    }

    private async Task<Seed> SeedAsync()
    {
        await using var orders = await fixture.DatabaseAsync();
        await using var statusDatabase = await fixture.DatabaseAsync();
        await orders.Database.MigrateAsync();
        await using var statuses = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(statusDatabase.Database.GetConnectionString()).Options);
        await statuses.Database.MigrateAsync();
        var order = new Order { Name = "Owned recovery fixture", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned.stl" });
        await orders.SaveChangesAsync();
        var initial = new OrderStatus { Name = "New" };
        var target = new OrderStatus { Name = "Quoted" };
        var history = new OrderStatusHistory { OrderId = order.Id, OrderStatus = initial };
        statuses.AddRange(history, target, new OrderStatusTransition { OrderStatus = initial, PossibleStatus = target });
        await statuses.SaveChangesAsync();
        return new Seed(orders.Database.GetConnectionString()!, statusDatabase.Database.GetConnectionString()!, order.Id, history.Id, target.Id, order.ProcessId, await orders.Files.Select(x => x.Id).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryReassignment_AcquiresBothOrderFencesInAscendingOrder(bool reverse)
    {
        var seed = await SeedAsync();
        await using var holder = Orders(seed);
        var other = new Order { Name = "Owned second lifetime", Quantity = 1, ProcessId = seed.ProcessId };
        holder.Add(other);
        await holder.SaveChangesAsync();
        var targetId = reverse ? seed.OrderId : other.Id;
        await using var statuses = Statuses(seed);
        if (reverse)
        {
            var history = await statuses.History.SingleAsync();
            history.OrderId = other.Id;
            await statuses.SaveChangesAsync();
        }
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var identity = $"legacy-order-lifetime\n{targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
        await using var writerOrders = Orders(seed);
        await using var writerStatuses = Statuses(seed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var action = Repository(writerOrders, writerStatuses).UpdateHistoryAsync(seed.HistoryId, new UpsertOrderStatusHistoryRequest(targetId, seed.TargetId), null, deadline.Token);
        try
        {
            await using var observer = new NpgsqlConnection(seed.OrderConnection);
            await observer.OpenAsync(deadline.Token);
            var blocked = false;
            while (!action.IsCompleted && !blocked)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() AND wait_event = 'advisory')", observer);
                blocked = (bool)(await command.ExecuteScalarAsync(deadline.Token))!;
                if (!blocked) await Task.Delay(20, deadline.Token);
            }
            Assert.True(blocked, "History reassignment bypassed target lifetime fence.");
        }
        finally
        {
            await transaction.RollbackAsync();
            Assert.Equal(UpdateResult.Updated, await action);
        }
    }

    [Fact]
    public async Task ConcurrentDeletion_CompletesOneStableReceiptWithoutOrphanedGraphs()
    {
        var seed = await SeedAsync();
        await using var firstOrders = Orders(seed);
        await using var firstStatuses = Statuses(seed);
        await using var secondOrders = Orders(seed);
        await using var secondStatuses = Statuses(seed);
        var results = await Task.WhenAll(Repository(firstOrders, firstStatuses).DeleteOrderAsync(seed.OrderId, default), Repository(secondOrders, secondStatuses).DeleteOrderAsync(seed.OrderId, default));
        Assert.Contains(true, results);
        var receipt = Assert.Single(await firstOrders.DeletionIntents.AsNoTracking().ToListAsync());
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Empty(await firstStatuses.History.AsNoTracking().ToListAsync());
        Assert.Empty(await firstOrders.Files.AsNoTracking().ToListAsync());
        Assert.False(await firstOrders.Orders.AsNoTracking().AnyAsync(x => x.Id == seed.OrderId));
    }

    private static async Task TombstoneAsync(OrderDbContext orders, int id)
    {
        await using var transaction = await orders.Database.BeginTransactionAsync();
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = id, DeletionId = Guid.NewGuid(), RequestedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow });
        await orders.SaveChangesAsync();
        await orders.Files.Where(x => x.OrderId == id).ExecuteDeleteAsync();
        await orders.Orders.Where(x => x.Id == id).ExecuteDeleteAsync();
        await transaction.CommitAsync();
    }

    [Theory]
    [InlineData("empty-id", "delete")]
    [InlineData("empty-id", "history")]
    [InlineData("empty-id", "worker")]
    [InlineData("missing-cleanup", "delete")]
    [InlineData("missing-cleanup", "history")]
    [InlineData("missing-cleanup", "worker")]
    public async Task MalformedIntent_IsNotLifetimeAuthorityOrCleanupPermission(string invalid, string operation)
    {
        var seed = await SeedAsync();
        await using var o = Orders(seed);
        await using var s = Statuses(seed);
        await TombstoneAsync(o, seed.OrderId);
        if (invalid == "empty-id") await o.DeletionIntents.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeletionId, Guid.Empty));
        else await o.DeletionIntents.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CompletedAtUtc, DateTime.UtcNow));
        var repository = Repository(o, s);
        if (operation == "worker") Assert.Equal(0, await repository.RecoverPendingDeletionsAsync(1, default));
        else if (operation == "history") await Assert.ThrowsAsync<OrderDeletionConflictException>(() => repository.GetHistoryAsync(seed.OrderId, default));
        else await Assert.ThrowsAsync<OrderDeletionConflictException>(() => repository.DeleteOrderAsync(seed.OrderId, default));
        Assert.Single(await s.History.AsNoTracking().ToListAsync());
        Assert.Null((await o.DeletionIntents.AsNoTracking().SingleAsync()).StatusCleanupCompletedAtUtc);
    }

    [Fact]
    public async Task RecoveryQueue_RechecksDueStateAfterLifetimeFence_NotTentativeCandidate()
    {
        var seed = await SeedAsync();
        await using var setup = Orders(seed);
        await TombstoneAsync(setup, seed.OrderId);
        var barrier = new RescheduleBeforeFence(seed);
        await using var o = Orders(seed, barrier);
        await using var s = Statuses(seed);
        Assert.Equal(0, await Repository(o, s).RecoverPendingDeletionsAsync(1, default));
        Assert.True(barrier.ObservedCandidate);
        Assert.Null((await setup.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
        Assert.Single(await s.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RecoveryQueue_BusyLifetimeSkipsAndCallerCancellationPropagatesWithoutWrites()
    {
        var seed = await SeedAsync();
        await using var holder = Orders(seed);
        await TombstoneAsync(holder, seed.OrderId);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var identity = $"legacy-order-lifetime\n{seed.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
        await using var o = Orders(seed);
        await using var s = Statuses(seed);
        var repository = Repository(o, s);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        Assert.Equal(0, await repository.RecoverPendingDeletionsAsync(1, deadline.Token));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.RecoverPendingDeletionsAsync(1, canceled.Token));
        Assert.Null((await o.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
        Assert.Single(await s.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RecoveryQueue_FailureBackoffIsDurableBoundedAndExactReceiptIsNotReplaced()
    {
        var seed = await SeedAsync();
        await using var o = Orders(seed);
        await using var s = Statuses(seed);
        await TombstoneAsync(o, seed.OrderId);
        var original = await o.DeletionIntents.AsNoTracking().SingleAsync();
        await s.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_owned_queue_cleanup() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'owned queue fault'; END; $$ LANGUAGE plpgsql; CREATE TRIGGER reject_owned_queue_cleanup BEFORE DELETE ON \"OrderStatusHistory\" FOR EACH ROW EXECUTE FUNCTION reject_owned_queue_cleanup();");
        var repository = Repository(o, s);
        var before = DateTime.UtcNow;
        Assert.Equal(0, await repository.RecoverPendingDeletionsAsync(1, default, 7));
        var first = await o.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(original.DeletionId, first.DeletionId);
        Assert.Equal(1, first.AttemptCount);
        Assert.InRange(first.NextAttemptAtUtc, before.AddSeconds(2), DateTime.UtcNow.AddSeconds(2));
        await o.DeletionIntents.ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AttemptCount, 10).SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow));
        before = DateTime.UtcNow;
        Assert.Equal(0, await repository.RecoverPendingDeletionsAsync(1, default, 7));
        var capped = await o.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(11, capped.AttemptCount);
        Assert.Equal(original.DeletionId, capped.DeletionId);
        Assert.InRange(capped.NextAttemptAtUtc, before.AddSeconds(7), DateTime.UtcNow.AddSeconds(7));
        Assert.Null(capped.CompletedAtUtc);
        Assert.Single(await s.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CustomerCancellation_AlreadyCancelledProjectionStillAcquiresLifetimeFence()
    {
        var seed = await SeedAsync();
        await using var holder = Orders(seed);
        await using var s = Statuses(seed);
        await holder.Orders.Where(x => x.Id == seed.OrderId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CustomerId, 42).SetProperty(x => x.AllowCancellation, true));
        await s.Statuses.Where(x => x.Id != seed.TargetId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Name, "Cancelled"));
        await using var transaction = await holder.Database.BeginTransactionAsync();
        var identity = $"legacy-order-lifetime\n{seed.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
        await using var o = Orders(seed);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var action = Repository(o, s).CancelCustomerOrderAsync(42, seed.OrderId, deadline.Token);
        try
        {
            await using var observer = new NpgsqlConnection(seed.OrderConnection);
            await observer.OpenAsync(deadline.Token);
            var blocked = false;
            while (!action.IsCompleted && !blocked)
            {
                await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event='advisory')", observer);
                blocked = (bool)(await command.ExecuteScalarAsync(deadline.Token))!;
                if (!blocked && !action.IsCompleted) await Task.Delay(20, deadline.Token);
            }
            Assert.True(blocked, "Already-cancelled projection bypassed the Order lifetime fence.");
        }
        finally { await transaction.CommitAsync(); await action; }
        Assert.Equal(UpdateResult.Updated, await action);
        Assert.False((await holder.Orders.AsNoTracking().SingleAsync()).AllowCancellation);
    }

    private sealed class RescheduleBeforeFence(Seed seed) : DbCommandInterceptor
    {
        public bool ObservedCandidate;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!ObservedCandidate && command.CommandText.Contains("pg_try_advisory_xact_lock", StringComparison.Ordinal))
            {
                ObservedCandidate = true;
                await using var scheduler = Orders(seed);
                await scheduler.DeletionIntents.Where(x => x.OrderId == seed.OrderId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(1)), cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NormalPairedContextRegistration_PreservesConfiguredRetryCombinations(bool orderRetry, bool statusRetry)
    {
        var seed = await SeedAsync();
        var services = new ServiceCollection();
        services.AddDbContext<OrderDbContext>(o => o.UseNpgsql(seed.OrderConnection, n => { if (orderRetry) n.EnableRetryOnFailure(); }));
        services.AddDbContext<OrderStatusDbContext>(o => o.UseNpgsql(seed.StatusConnection, n => { if (statusRetry) n.EnableRetryOnFailure(); }));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var o = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var s = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Equal(orderRetry, o.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal(statusRetry, s.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal(UpdateResult.Updated, await Repository(o, s).UpdateHistoryAsync(seed.HistoryId, new(seed.OrderId, seed.TargetId), null, default));
        Assert.Equal(seed.TargetId, (await s.History.AsNoTracking().SingleAsync()).OrderStatusId);
    }

    [Theory]
    [InlineData("history-put", "ack")]
    [InlineData("history-delete", "ack")]
    [InlineData("transition", "ack")]
    [InlineData("history-put", "dispose")]
    [InlineData("history-delete", "dispose")]
    [InlineData("transition", "dispose")]
    [InlineData("history-put", "strategy-dispose")]
    [InlineData("history-delete", "strategy-dispose")]
    public async Task ReceiptlessMutation_CommitOrTeardownFaultNeverReplaysSubmittedWrite(string operation, string faultKind)
    {
        var seed = await SeedAsync();
        var fault = new ReceiptlessFault(faultKind);
        await using var o = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(seed.OrderConnection, n => n.EnableRetryOnFailure(1, TimeSpan.Zero, null)).Options);
        await using var s = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(seed.StatusConnection, n => n.EnableRetryOnFailure(1, TimeSpan.Zero, null)).AddInterceptors(fault.Commit, fault.Disposal).Options);
        var repository = Repository(o, s);
        var error = await Record.ExceptionAsync(async () =>
        {
            if (operation == "history-put") await repository.UpdateHistoryAsync(seed.HistoryId, new(seed.OrderId, seed.TargetId), null, default);
            else if (operation == "history-delete") await repository.DeleteHistoryAsync(seed.HistoryId, default);
            else await repository.TransitionAsync(seed.OrderId, seed.TargetId, default);
        });
        Assert.True(fault.Injected, "Owned commit/teardown barrier was not reached.");
        Assert.Equal(1, fault.SubmittedCommits);
        Assert.NotNull(error);
        Assert.Equal("OrderMutationUnavailableException", error.GetType().Name);
        await using var fresh = Statuses(seed);
        var history = await fresh.History.AsNoTracking().ToListAsync();
        if (operation == "history-delete") Assert.Empty(history);
        else if (operation == "transition") Assert.Equal(2, history.Count);
        else Assert.Equal(seed.TargetId, Assert.Single(history).OrderStatusId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileMutation_OwnerObservationTeardownAfterCommitIsUnavailableWithoutReplay(bool delete)
    {
        var seed = await SeedAsync();
        var fault = new ReceiptlessFault("strategy-dispose");
        await using var o = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(seed.OrderConnection, n => n.EnableRetryOnFailure(1, TimeSpan.Zero, null)).AddInterceptors(fault.Commit, fault.Disposal).Options);
        await using var s = Statuses(seed);
        var repository = Repository(o, s);
        var error = await Record.ExceptionAsync(async () =>
        {
            if (delete) await repository.DeleteFileAsync(seed.FileId, default);
            else await repository.UpdateFileAsync(seed.FileId, new(null, "owned-update", "updated.stl"), default);
        });
        Assert.True(fault.Injected, "Owned post-commit observation teardown barrier was not reached.");
        Assert.Equal(1, fault.SubmittedCommits);
        Assert.IsType<OrderMutationUnavailableException>(error);
        await using var fresh = Orders(seed);
        var files = await fresh.Files.AsNoTracking().ToListAsync();
        if (delete) Assert.Empty(files);
        else Assert.Equal("updated.stl", Assert.Single(files).ObjectName);
        await using var status = Statuses(seed);
        Assert.Single(await status.History.AsNoTracking().ToListAsync());
    }

    private sealed class ReceiptlessFault(string kind)
    {
        public int SubmittedCommits;
        public bool Injected;
        public int CommittedDisposals;
        public DbTransactionInterceptor Commit => new CommitObserver(this, kind);
        public DbConnectionInterceptor Disposal => new DisposalFault(this, kind);
        private sealed class CommitObserver(ReceiptlessFault owner, string faultKind) : DbTransactionInterceptor
        {
            public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                owner.SubmittedCommits++;
                if (faultKind == "ack" && !owner.Injected)
                {
                    owner.Injected = true;
                    throw new NpgsqlException("Owned receiptless lost acknowledgement", new IOException("Owned interruption"));
                }
                return Task.CompletedTask;
            }
        }
        private sealed class DisposalFault(ReceiptlessFault owner, string faultKind) : DbConnectionInterceptor
        {
            public override ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            {
                if (owner.SubmittedCommits != 0) owner.CommittedDisposals++;
                if ((faultKind == "dispose" || faultKind == "strategy-dispose" && owner.CommittedDisposals == 2)
                    && owner.SubmittedCommits != 0 && !owner.Injected)
                {
                    owner.Injected = true;
                    throw new NpgsqlException("Owned receiptless teardown interruption", new IOException("Owned interruption"));
                }
                return ValueTask.FromResult(result);
            }
        }
    }

    private static OrderDbContext Orders(Seed seed, params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(seed.OrderConnection).AddInterceptors(interceptors).Options);
    private static OrderStatusDbContext Statuses(Seed seed) => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(seed.StatusConnection).Options);
    private static OrderRepository Repository(OrderDbContext orders, OrderStatusDbContext statuses) => new(orders, statuses, new OwnedMemoryCache(), TimeProvider.System);
    private sealed record Seed(string OrderConnection, string StatusConnection, int OrderId, int HistoryId, int TargetId, int ProcessId, int FileId);

    private sealed class LoseCommitAcknowledgement : DbTransactionInterceptor
    {
        private int injected;
        public bool ObservedDurableCommit { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref injected, 1) == 0)
            {
                ObservedDurableCommit = true;
                throw new NpgsqlException("Owned lost commit acknowledgement", new IOException("Owned transport interruption"));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class OwnedMemoryCache : IOrderCache
    {
        private object? value;
        public Task<T?> GetAsync<T>(string k, CancellationToken c) where T : class { c.ThrowIfCancellationRequested(); return Task.FromResult(value as T); }
        public Task SetAsync<T>(string k, T v, TimeSpan t, CancellationToken c) where T : class { c.ThrowIfCancellationRequested(); value = v; return Task.CompletedTask; }
        public Task RemoveAsync(string k, CancellationToken c) { c.ThrowIfCancellationRequested(); value = null; return Task.CompletedTask; }
    }
}
