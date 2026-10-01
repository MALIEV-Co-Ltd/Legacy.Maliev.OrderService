using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data;

public sealed partial class OrderRepository
{
    public async Task<OrderFileResponse?> GetFileAsync(int id, CancellationToken c)
    {
        var file = await ProjectFiles(orders.Files.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(c);
        if (file is null) return null;
        await using var authority = FreshOrders();
        return await IsDeletedLifetimeAsync(authority, file.OrderId, c) ? null : file;
    }

    public async Task<IReadOnlyList<OrderFileResponse>> GetFilesAsync(int orderId, CancellationToken c)
    {
        await using var authority = FreshOrders();
        if (await IsDeletedLifetimeAsync(authority, orderId, c)) return [];
        return await ProjectFiles(orders.Files.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id)).ToListAsync(c);
    }

    public async Task<UpdateResult> UpdateOrderAsync(int id, UpsertOrderRequest r, DateTimeOffset? expected, CancellationToken c)
    {
        var result = await WithLifetimeAsync([id], async (o, s, _) =>
        {
            if (await IsDeletedLifetimeAsync(o, id, c)) return UpdateResult.NotFound;
            var row = await o.Orders.SingleOrDefaultAsync(x => x.Id == id, c);
            if (row is null) return UpdateResult.NotFound;
            if (expected is not null) o.Entry(row).Property(x => x.ModifiedDate).OriginalValue = DateTime.SpecifyKind(expected.Value.UtcDateTime, DateTimeKind.Unspecified);
            var accepted = await s.History.Where(x => x.OrderId == id).OrderByDescending(x => x.Id)
                .Select(x => EF.Functions.ILike(x.OrderStatus!.Name!, "Accepted")).FirstOrDefaultAsync(c);
            Map(row, r).ModifiedDate = Now();
            if (accepted) row.AllowCancellation = false;
            try { await o.SaveChangesAsync(c); return UpdateResult.Updated; }
            catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; }
        }, c);
        if (result == UpdateResult.Updated) await cache.RemoveAsync(Key(id), c);
        return result;
    }

    public Task<OrderFileResponse?> CreateFileAsync(int orderId, string bucket, string objectName, CancellationToken c) =>
        WithLifetimeAsync<OrderFileResponse?>([orderId], async (o, _, _) =>
        {
            if (await IsDeletedLifetimeAsync(o, orderId, c) || !await o.Orders.AnyAsync(x => x.Id == orderId, c)) return null;
            var normalizedBucket = bucket.Trim();
            var normalizedObjectName = objectName.Trim();
            var lockIdentity = $"order-file\n{orderId}\n{normalizedBucket}\n{normalizedObjectName}";
            await o.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", c);
            var existing = await ProjectFiles(o.Files.AsNoTracking().Where(x => x.OrderId == orderId && x.Bucket == normalizedBucket && x.ObjectName == normalizedObjectName).OrderBy(x => x.Id)).FirstOrDefaultAsync(c);
            if (existing is not null) return existing;
            var now = Now();
            var entity = new OrderFile { OrderId = orderId, Bucket = normalizedBucket, ObjectName = normalizedObjectName, CreatedDate = now, ModifiedDate = now };
            o.Add(entity);
            await o.SaveChangesAsync(c);
            return File(entity);
        }, c, false);

    public async Task<bool> DeleteFileAsync(int id, CancellationToken c)
    {
        var returned = false;
        try
        {
            await using var observation = FreshOrders();
            var original = await observation.Files.AsNoTracking().Where(x => x.Id == id).Select(x => (int?)x.OrderId).SingleOrDefaultAsync(c);
            if (original is null) return false;
            var result = await WithLifetimeAsync([original.Value], async (o, _, _) =>
            {
                var row = await o.Files.SingleOrDefaultAsync(x => x.Id == id, c);
                if (row is null || row.OrderId != original.Value || await IsDeletedLifetimeAsync(o, original.Value, c)) return false;
                o.Remove(row);
                await o.SaveChangesAsync(c);
                return true;
            }, c, false);
            returned = true;
            return result;
        }
        catch (Exception) when (returned && !c.IsCancellationRequested) { throw new Legacy.Maliev.OrderService.Application.Interfaces.OrderMutationUnavailableException(); }
    }

    public async Task<bool> UpdateFileAsync(int id, UpsertOrderFileRequest r, CancellationToken c)
    {
        var returned = false;
        try
        {
            await using var observation = FreshOrders();
            var original = await observation.Files.AsNoTracking().Where(x => x.Id == id).Select(x => (int?)x.OrderId).SingleOrDefaultAsync(c);
            if (original is null) return false;
            var destination = r.OrderId ?? original.Value;
            var result = await WithLifetimeAsync([original.Value, destination], async (o, _, _) =>
            {
                var row = await o.Files.SingleOrDefaultAsync(x => x.Id == id, c);
                if (row is null || row.OrderId != original.Value) return false;
                if (await IsDeletedLifetimeAsync(o, original.Value, c) || await IsDeletedLifetimeAsync(o, destination, c)) return false;
                row.OrderId = destination;
                row.Bucket = r.Bucket.Trim();
                row.ObjectName = r.ObjectName.Trim();
                row.ModifiedDate = Now();
                await o.SaveChangesAsync(c);
                return true;
            }, c, false);
            returned = true;
            return result;
        }
        catch (Exception) when (returned && !c.IsCancellationRequested) { throw new Legacy.Maliev.OrderService.Application.Interfaces.OrderMutationUnavailableException(); }
    }
}
