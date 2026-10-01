using System.Data;
using System.Data.Common;
using System.Globalization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Legacy.Maliev.OrderService.Data;

public sealed partial class OrderRepository
{
    private OrderDbContext FreshOrders() => new((DbContextOptions<OrderDbContext>)orders.GetService<IDbContextOptions>());
    private OrderStatusDbContext FreshStatuses() => new((DbContextOptions<OrderStatusDbContext>)statuses.GetService<IDbContextOptions>());
    private static string LifetimeKey(int id) => $"legacy-order-lifetime\n{id.ToString(CultureInfo.InvariantCulture)}";
    private static Task FenceAsync(OrderDbContext context, int id, CancellationToken c) => context.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({LifetimeKey(id)}, 0))", c);

    public async Task<OrderDeletionResult> DeleteOrderWithRecoveryAsync(int id, CancellationToken cancellationToken)
    {
        var deletionId = Guid.NewGuid();
        try
        {
            // The operation identity survives execution-strategy attempts and lost acknowledgements.
            await using var strategyContext = FreshOrders();
            var admitted = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var attempt = FreshOrders();
                if (!await OrderDeletionSchemaReadiness.CheckAsync(attempt, cancellationToken)) return OrderDeletionResult.Unavailable;
                await using var transaction = await attempt.Database.BeginTransactionAsync(cancellationToken);
                await FenceAsync(attempt, id, cancellationToken);
                var receipt = await attempt.DeletionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id, cancellationToken);
                var exists = await attempt.Orders.AnyAsync(x => x.Id == id, cancellationToken);
                if (receipt is not null)
                {
                    if (!IsValidReceipt(receipt) || exists) return OrderDeletionResult.Conflict;
                    deletionId = receipt.DeletionId;
                    return receipt.CompletedAtUtc is null ? OrderDeletionResult.Deleted : OrderDeletionResult.NotFound;
                }
                if (!exists) return OrderDeletionResult.NotFound;
                var now = clock.GetUtcNow().UtcDateTime;
                attempt.DeletionIntents.Add(new OrderDeletionIntent
                {
                    OrderId = id,
                    DeletionId = deletionId,
                    RequestedAtUtc = now,
                    NextAttemptAtUtc = now,
                });
                await attempt.SaveChangesAsync(cancellationToken);
                await attempt.Files.Where(x => x.OrderId == id).ExecuteDeleteAsync(cancellationToken);
                await attempt.Orders.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return OrderDeletionResult.Deleted;
            });
            if (admitted != OrderDeletionResult.Deleted) return admitted;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsDatabaseFailure(exception))
        {
            // Missing Order alone is never a commit receipt. Verify this exact operation freshly.
            try
            {
                await using var verification = FreshOrders();
                var receipt = await verification.DeletionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id && x.DeletionId == deletionId, cancellationToken);
                if (receipt is null) return OrderDeletionResult.Unavailable;
                if (!IsValidReceipt(receipt) || await verification.Orders.AnyAsync(x => x.Id == id, cancellationToken)) return OrderDeletionResult.Conflict;
            }
            catch (Exception verificationFailure) when (!cancellationToken.IsCancellationRequested && IsDatabaseFailure(verificationFailure)) { return OrderDeletionResult.Unavailable; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await CompleteDeletionAsync(id, deletionId, false, cancellationToken);
    }

    private async Task<OrderDeletionResult> CompleteDeletionAsync(int id, Guid deletionId, bool tryFence, CancellationToken c, int maxBackoffSeconds = 300)
    {
        try
        {
            await using var strategyContext = FreshOrders();
            var result = await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var attempt = FreshOrders();
                if (!await OrderDeletionSchemaReadiness.CheckAsync(attempt, c)) return OrderDeletionResult.Unavailable;
                await using var transaction = await attempt.Database.BeginTransactionAsync(c);
                if (tryFence)
                {
                    var acquired = await attempt.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({LifetimeKey(id)}, 0)) AS \"Value\"").SingleAsync(c);
                    if (!acquired) return OrderDeletionResult.Unavailable;
                }
                else await FenceAsync(attempt, id, c);
                // Lifetime fence precedes queue-row locking; tentative queue candidates convey no authority.
                var receipt = await attempt.DeletionIntents.FromSqlInterpolated(
                    $"SELECT * FROM \"OrderDeletionIntent\" WHERE \"OrderId\" = {id} AND \"DeletionId\" = {deletionId} FOR UPDATE SKIP LOCKED").SingleOrDefaultAsync(c);
                if (receipt is null || !IsValidReceipt(receipt)) return OrderDeletionResult.Conflict;
                if (await attempt.Orders.AnyAsync(x => x.Id == id, c)) return OrderDeletionResult.Conflict;
                if (tryFence && (receipt.CompletedAtUtc is not null || receipt.NextAttemptAtUtc > clock.GetUtcNow().UtcDateTime)) return OrderDeletionResult.NotFound;
                if (receipt.CompletedAtUtc is null)
                {
                    await using var statusStrategy = FreshStatuses();
                    await statusStrategy.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                    {
                        await using var statusAttempt = FreshStatuses();
                        await using var statusTransaction = await statusAttempt.Database.BeginTransactionAsync(IsolationLevel.Serializable, c);
                        await statusAttempt.History.Where(x => x.OrderId == id).ExecuteDeleteAsync(c);
                        await statusTransaction.CommitAsync(c);
                    });
                    receipt.StatusCleanupCompletedAtUtc = clock.GetUtcNow().UtcDateTime;
                    receipt.CompletedAtUtc = receipt.StatusCleanupCompletedAtUtc;
                    await attempt.SaveChangesAsync(c);
                    await transaction.CommitAsync(c);
                }
                return OrderDeletionResult.Deleted;
            });
            if (result == OrderDeletionResult.Deleted) await cache.RemoveAsync(Key(id), c);
            return result;
        }
        catch (Exception exception) when (!c.IsCancellationRequested && IsDatabaseFailure(exception))
        {
            try
            {
                await using var verification = FreshOrders();
                var receipt = await verification.DeletionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id && x.DeletionId == deletionId, c);
                if (receipt is null) return OrderDeletionResult.Unavailable;
                if (!IsValidReceipt(receipt) || await verification.Orders.AnyAsync(x => x.Id == id, c)) return OrderDeletionResult.Conflict;
                if (receipt.CompletedAtUtc is not null)
                {
                    await cache.RemoveAsync(Key(id), c);
                    return OrderDeletionResult.Deleted;
                }
                await RecordCleanupFailureAsync(id, deletionId, maxBackoffSeconds, c);
                return OrderDeletionResult.Unavailable;
            }
            catch (Exception verificationFailure) when (!c.IsCancellationRequested && IsDatabaseFailure(verificationFailure)) { return OrderDeletionResult.Unavailable; }
        }
    }

    public async Task<int> RecoverPendingDeletionsAsync(int batchSize, CancellationToken c, int maxBackoffSeconds = 300)
    {
        if (batchSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(batchSize));
        if (maxBackoffSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(maxBackoffSeconds));
        await using var context = FreshOrders();
        if (!await OrderDeletionSchemaReadiness.CheckAsync(context, c)) return 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var candidates = await context.DeletionIntents.AsNoTracking().Where(x => x.CompletedAtUtc == null && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.NextAttemptAtUtc).ThenBy(x => x.OrderId).Take(batchSize)
            .Select(x => new { x.OrderId, x.DeletionId }).ToListAsync(c);
        var completed = 0;
        foreach (var candidate in candidates)
        {
            if (await CompleteDeletionAsync(candidate.OrderId, candidate.DeletionId, true, c, maxBackoffSeconds) == OrderDeletionResult.Deleted) completed++;
        }
        return completed;
    }

    private async Task RecordCleanupFailureAsync(int id, Guid deletionId, int maximum, CancellationToken c)
    {
        await using var strategyContext = FreshOrders();
        await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var attempt = FreshOrders();
            await using var transaction = await attempt.Database.BeginTransactionAsync(c);
            var acquired = await attempt.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({LifetimeKey(id)}, 0)) AS \"Value\"").SingleAsync(c);
            if (!acquired) return;
            var receipt = await attempt.DeletionIntents.SingleOrDefaultAsync(x => x.OrderId == id && x.DeletionId == deletionId && x.CompletedAtUtc == null, c);
            if (receipt is null || !IsValidReceipt(receipt) || await attempt.Orders.AnyAsync(x => x.Id == id, c)) return;
            receipt.AttemptCount = Math.Min(receipt.AttemptCount, int.MaxValue - 1) + 1;
            var seconds = Math.Min(maximum, Math.Pow(2, Math.Min(receipt.AttemptCount, 9)));
            receipt.NextAttemptAtUtc = clock.GetUtcNow().UtcDateTime.AddSeconds(seconds);
            await attempt.SaveChangesAsync(c);
            await transaction.CommitAsync(c);
        });
    }

    private static bool IsDatabaseFailure(Exception exception) => exception is DbException or DbUpdateException
        || exception is InvalidOperationException { InnerException: DbException };

    private static bool IsValidReceipt(OrderDeletionIntent receipt) => receipt.DeletionId != Guid.Empty
        && receipt.RequestedAtUtc != default && receipt.NextAttemptAtUtc >= receipt.RequestedAtUtc
        && receipt.AttemptCount >= 0
        && (receipt.StatusCleanupCompletedAtUtc is null || receipt.StatusCleanupCompletedAtUtc >= receipt.RequestedAtUtc)
        && (receipt.CompletedAtUtc is null || (receipt.StatusCleanupCompletedAtUtc is not null && receipt.CompletedAtUtc >= receipt.StatusCleanupCompletedAtUtc));

    private async Task<bool> IsDeletedLifetimeAsync(OrderDbContext context, int id, CancellationToken c)
    {
        var receipt = await context.DeletionIntents.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == id, c);
        if (receipt is null) return false;
        if (!IsValidReceipt(receipt)) throw new OrderDeletionConflictException();
        if (await context.Orders.AnyAsync(x => x.Id == id, c)) throw new OrderDeletionConflictException();
        return true;
    }

    private async Task<T> WithLifetimeAsync<T>(IEnumerable<int> ids, Func<OrderDbContext, OrderStatusDbContext, Action, Task<T>> operation, CancellationToken c, bool needsStatusTransaction = true)
    {
        var anySubmitted = false;
        try
        {
            await using var strategyContext = FreshOrders();
            await using var statusStrategyContext = FreshStatuses();
            var strategy = strategyContext.Database.CreateExecutionStrategy();
            if (needsStatusTransaction && !strategy.RetriesOnFailure)
            {
                // Existing callers may configure retry only on Status. Its configured ambient
                // strategy must cover the complete fresh fenced unit, not a reused Status context.
                var statusStrategy = statusStrategyContext.Database.CreateExecutionStrategy();
                if (statusStrategy.RetriesOnFailure) strategy = statusStrategy;
            }
            return await strategy.ExecuteAsync(async () =>
            {
                var submitted = false;
                var rollbackProven = true; // Before any transaction starts, this attempt cannot have written.
                try
                {
                    await using var orderAttempt = FreshOrders();
                    await using var statusAttempt = FreshStatuses();
                    await using var orderTransaction = await orderAttempt.Database.BeginTransactionAsync(c);
                    rollbackProven = false;
                    try
                    {
                        foreach (var id in ids.Distinct().Order()) await FenceAsync(orderAttempt, id, c);
                        await using var statusTransaction = needsStatusTransaction ? await statusAttempt.Database.BeginTransactionAsync(IsolationLevel.Serializable, c) : null;
                        try
                        {
                            var result = await operation(orderAttempt, statusAttempt, () => { submitted = true; anySubmitted = true; });
                            if (statusAttempt.Database.CurrentTransaction is not null)
                            {
                                submitted = true;
                                anySubmitted = true;
                                await statusTransaction!.CommitAsync(c);
                            }
                            if (orderAttempt.Database.CurrentTransaction is not null)
                            {
                                submitted = true;
                                anySubmitted = true;
                                await orderTransaction.CommitAsync(c);
                            }
                            return result;
                        }
                        catch
                        {
                            if (!submitted && statusAttempt.Database.CurrentTransaction is not null) await statusTransaction!.RollbackAsync(c);
                            throw;
                        }
                    }
                    catch
                    {
                        if (!submitted && orderAttempt.Database.CurrentTransaction is not null)
                        {
                            await orderTransaction.RollbackAsync(c);
                            rollbackProven = true;
                        }
                        throw;
                    }
                }
                catch (Exception exception) when (!c.IsCancellationRequested && (submitted || (!rollbackProven && IsDatabaseFailure(exception))))
                {
                    // The catch encloses context/transaction teardown too. Receiptless mutations
                    // must not be replayed after a submitted commit, even if disposal looks transient.
                    throw new OrderMutationUnavailableException();
                }
            });
        }
        catch (Exception) when (anySubmitted && !c.IsCancellationRequested) { throw new OrderMutationUnavailableException(); }
    }
}
