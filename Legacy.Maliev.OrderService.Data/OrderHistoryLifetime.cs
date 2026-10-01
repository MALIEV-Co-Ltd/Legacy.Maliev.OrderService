using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data;

public sealed partial class OrderRepository
{
    public async Task<bool> DeleteHistoryAsync(int id, CancellationToken c)
    {
        var returned = false;
        try
        {
            await using var observation = FreshStatuses();
            var original = await observation.History.AsNoTracking().Where(x => x.Id == id).Select(x => (int?)x.OrderId).SingleOrDefaultAsync(c);
            if (original is null) return false;
            var result = await WithLifetimeAsync([original.Value], async (o, s, _) =>
            {
                var row = await s.History.SingleOrDefaultAsync(x => x.Id == id, c);
                if (row is null || row.OrderId != original.Value || await IsDeletedLifetimeAsync(o, original.Value, c)) return false;
                s.Remove(row);
                await s.SaveChangesAsync(c);
                return true;
            }, c);
            returned = true;
            return result;
        }
        catch (Exception) when (returned && !c.IsCancellationRequested) { throw new Legacy.Maliev.OrderService.Application.Interfaces.OrderMutationUnavailableException(); }
    }

    public async Task<UpdateResult> UpdateHistoryAsync(int id, UpsertOrderStatusHistoryRequest r, DateTimeOffset? expected, CancellationToken c)
    {
        var returned = false;
        try
        {
            await using var observation = FreshStatuses();
            var original = await observation.History.AsNoTracking().Where(x => x.Id == id).Select(x => (int?)x.OrderId).SingleOrDefaultAsync(c);
            if (original is null) return UpdateResult.NotFound;
            var result = await WithLifetimeAsync([original.Value, r.OrderId], async (o, s, _) =>
            {
                var row = await s.History.SingleOrDefaultAsync(x => x.Id == id, c);
                if (row is null) return UpdateResult.NotFound;
                if (row.OrderId != original.Value) return UpdateResult.Conflict;
                if (await IsDeletedLifetimeAsync(o, original.Value, c) || await IsDeletedLifetimeAsync(o, r.OrderId, c)) return UpdateResult.NotFound;
                if (expected is not null) s.Entry(row).Property(x => x.ModifiedDate).OriginalValue = DateTime.SpecifyKind(expected.Value.UtcDateTime, DateTimeKind.Unspecified);
                row.OrderId = r.OrderId;
                row.OrderStatusId = r.OrderStatusId;
                row.ModifiedDate = Now();
                try { await s.SaveChangesAsync(c); return UpdateResult.Updated; }
                catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; }
            }, c);
            returned = true;
            return result;
        }
        catch (Exception) when (returned && !c.IsCancellationRequested) { throw new Legacy.Maliev.OrderService.Application.Interfaces.OrderMutationUnavailableException(); }
    }

    public async Task<OrderStatusResponse?> GetLatestStatusAsync(int orderId, CancellationToken c)
    {
        await using var authority = FreshOrders();
        if (await IsDeletedLifetimeAsync(authority, orderId, c)) return null;
        return await statuses.History.AsNoTracking().Where(x => x.OrderId == orderId).OrderByDescending(x => x.Id)
            .Select(x => new OrderStatusResponse(x.OrderStatus!.Id, x.OrderStatus.Name, x.OrderStatus.Description, x.CreatedDate, x.ModifiedDate)).FirstOrDefaultAsync(c);
    }

    public async Task<IReadOnlyList<OrderStatusHistoryResponse>> GetHistoryAsync(int orderId, CancellationToken c)
    {
        await using var authority = FreshOrders();
        if (await IsDeletedLifetimeAsync(authority, orderId, c)) return [];
        return await statuses.History.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.CreatedDate)
            .Select(x => new OrderStatusHistoryResponse(x.Id, x.OrderId, x.OrderStatusId, x.OrderStatus!.Name, x.OrderStatus.Description, x.CreatedDate, x.ModifiedDate)).ToListAsync(c);
    }
}
