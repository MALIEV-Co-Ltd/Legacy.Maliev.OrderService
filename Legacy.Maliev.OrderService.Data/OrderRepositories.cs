using System.Data;
using System.Data.Common;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.OrderService.Data;

public sealed class OrderRepository(OrderDbContext orders, OrderStatusDbContext statuses, IOrderCache cache, TimeProvider clock) : IOrderService
{
    public async Task<OrderResponse> CreateOrderAsync(UpsertOrderRequest r, CancellationToken c) { var n = Now(); var e = Map(new Order(), r); e.CreatedDate = n; e.ModifiedDate = n; orders.Add(e); await orders.SaveChangesAsync(c); return await ReadOrder(e.Id, c) ?? throw new InvalidOperationException(); }
    public async Task<bool> DeleteOrderAsync(int id, CancellationToken c) { var d = await orders.Orders.Where(x => x.Id == id).ExecuteDeleteAsync(c) == 1; if (d) await cache.RemoveAsync(Key(id), c); return d; }
    public async Task<OrderResponse?> GetOrderAsync(int id, CancellationToken c) { var v = await cache.GetAsync<OrderResponse>(Key(id), c); if (v is not null) return v; v = await ReadOrder(id, c); if (v is not null) await cache.SetAsync(Key(id), v, TimeSpan.FromMinutes(2), c); return v; }
    public async Task<PaginatedResponse<OrderResponse>?> GetOrdersAsync(int? customerId, bool pending, OrderSortType? sort, string? search, int page, int size, CancellationToken c)
    {
        IQueryable<Order> q = orders.Orders.AsNoTracking();
        if (customerId is not null) q = q.Where(x => x.CustomerId == customerId);
        if (pending) q = q.Where(x => x.PromisedDate != null && x.FinishedDate == null);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var v = search.Trim();
            if (int.TryParse(v, out var id))
            {
                q = q.Where(x => x.Id == id);
            }
            else
            {
                var p = $"%{v}%";
                q = pending
                    ? q.Where(x =>
                        (x.Name != null && EF.Functions.ILike(x.Name, p))
                        || (x.Description != null && EF.Functions.ILike(x.Description, p)))
                    : q.Where(x =>
                        (x.Name != null && EF.Functions.ILike(x.Name, p))
                        || (x.Description != null && EF.Functions.ILike(x.Description, p))
                        || (x.TrackingNumber != null && EF.Functions.ILike(x.TrackingNumber, p))
                        || (x.Comment != null && EF.Functions.ILike(x.Comment, p)));
            }
        }

        q = sort switch
        {
            OrderSortType.OrderId_Descending => q.OrderByDescending(x => x.Id),
            OrderSortType.OrderCreatedDate_Ascending => q.OrderBy(x => x.CreatedDate).ThenBy(x => x.Id),
            OrderSortType.OrderCreatedDate_Descending => q.OrderByDescending(x => x.CreatedDate).ThenBy(x => x.Id),
            OrderSortType.OrderModifiedDate_Ascending => q.OrderBy(x => x.ModifiedDate).ThenBy(x => x.Id),
            OrderSortType.OrderModifiedDate_Descending => q.OrderByDescending(x => x.ModifiedDate).ThenBy(x => x.Id),
            OrderSortType.OrderRemaining_Ascending => q.OrderBy(x => x.Remaining).ThenBy(x => x.Id),
            OrderSortType.OrderRemaining_Descending => q.OrderByDescending(x => x.Remaining).ThenBy(x => x.Id),
            OrderSortType.OrderQuantity_Ascending => q.OrderBy(x => x.Quantity).ThenBy(x => x.Id),
            OrderSortType.OrderQuantity_Descending => q.OrderByDescending(x => x.Quantity).ThenBy(x => x.Id),
            _ => q.OrderBy(x => x.Id),
        };
        return await Page(ProjectOrders(q), page, size, c);
    }
    public async Task<UpdateResult> UpdateOrderAsync(int id, UpsertOrderRequest r, DateTimeOffset? expected, CancellationToken c) { var e = await orders.Orders.FindAsync([id], c); if (e is null) return UpdateResult.NotFound; if (expected is not null) orders.Entry(e).Property(x => x.ModifiedDate).OriginalValue = expected.Value.UtcDateTime; var accepted = await HasLatestStatusAsync(id, "Accepted", c); Map(e, r).ModifiedDate = Now(); if (accepted) e.AllowCancellation = false; try { await orders.SaveChangesAsync(c); await cache.RemoveAsync(Key(id), c); return UpdateResult.Updated; } catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; } }
    public async Task<CustomerOrderDetails?> GetCustomerOrderAsync(int customerId, int orderId, CancellationToken c) { var order = await ProjectOrders(orders.Orders.AsNoTracking().Where(x => x.Id == orderId && x.CustomerId == customerId)).SingleOrDefaultAsync(c); if (order is null) return null; var process = await GetProcessAsync(order.ProcessId, c); var history = await GetHistoryAsync(orderId, c); var files = await GetFilesAsync(orderId, c); return new(order, process, history, files); }
    public async Task<UpdateResult> CancelCustomerOrderAsync(int customerId, int orderId, CancellationToken c)
    {
        var order = await orders.Orders.SingleOrDefaultAsync(x => x.Id == orderId && x.CustomerId == customerId, c);
        if (order is null) return UpdateResult.NotFound;
        var latest = await GetLatestStatusAsync(orderId, c);
        if (string.Equals(latest?.Name, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateResult.InvalidTransition;
        }

        var alreadyCancelled = string.Equals(latest?.Name, "Cancelled", StringComparison.OrdinalIgnoreCase);
        if (!alreadyCancelled)
        {
            if (!order.AllowCancellation) return UpdateResult.InvalidTransition;
            var transition = await TransitionAsync(orderId, "Cancelled", c);
            if (transition != UpdateResult.Updated) return transition;
        }

        if (!order.AllowCancellation) return UpdateResult.Updated;
        order.AllowCancellation = false;
        order.ModifiedDate = Now();
        try
        {
            await orders.SaveChangesAsync(c);
            await cache.RemoveAsync(Key(orderId), c);
            return UpdateResult.Updated;
        }
        catch (DbUpdateConcurrencyException)
        {
            return UpdateResult.Conflict;
        }
    }
    public async Task<CategoryResponse> CreateCategoryAsync(UpsertCategoryRequest r, CancellationToken c) { var n = Now(); var e = new Category { Name = r.Name, CreatedDate = n, ModifiedDate = n }; orders.Add(e); await orders.SaveChangesAsync(c); return Cat(e); }
    public Task<bool> DeleteCategoryAsync(int id, CancellationToken c) => Delete(orders.Categories, id, c); public async Task<CategoryResponse?> GetCategoryAsync(int id, CancellationToken c) => await orders.Categories.AsNoTracking().Where(x => x.Id == id).Select(x => new CategoryResponse(x.Id, x.Name, x.CreatedDate, x.ModifiedDate)).SingleOrDefaultAsync(c); public async Task<bool> UpdateCategoryAsync(int id, UpsertCategoryRequest r, CancellationToken c) { var e = await orders.Categories.FindAsync([id], c); if (e is null) return false; e.Name = r.Name; e.ModifiedDate = Now(); await orders.SaveChangesAsync(c); return true; }
    public async Task<FileFormatResponse> CreateFileFormatAsync(UpsertFileFormatRequest r, CancellationToken c) { var n = Now(); var e = new FileFormat { Name = r.Name, Extension = r.Extension, CreatedDate = n, ModifiedDate = n }; orders.Add(e); await orders.SaveChangesAsync(c); return Format(e); }
    public Task<bool> DeleteFileFormatAsync(int id, CancellationToken c) => Delete(orders.FileFormats, id, c); public async Task<IReadOnlyList<FileFormatResponse>> GetFileFormatsAsync(CancellationToken c) => await orders.FileFormats.AsNoTracking().OrderBy(x => x.Id).Select(x => new FileFormatResponse(x.Id, x.Name, x.Extension, x.CreatedDate, x.ModifiedDate)).ToListAsync(c); public async Task<FileFormatResponse?> GetFileFormatAsync(int id, CancellationToken c) => await orders.FileFormats.AsNoTracking().Where(x => x.Id == id).Select(x => new FileFormatResponse(x.Id, x.Name, x.Extension, x.CreatedDate, x.ModifiedDate)).SingleOrDefaultAsync(c); public async Task<bool> UpdateFileFormatAsync(int id, UpsertFileFormatRequest r, CancellationToken c) { var e = await orders.FileFormats.FindAsync([id], c); if (e is null) return false; e.Name = r.Name; e.Extension = r.Extension; e.ModifiedDate = Now(); await orders.SaveChangesAsync(c); return true; }
    public async Task<ProcessResponse> CreateProcessAsync(UpsertProcessRequest r, CancellationToken c) { var n = Now(); var e = new Process { CategoryId = r.CategoryId, Name = r.Name.Trim(), CreatedDate = n, ModifiedDate = n }; orders.Add(e); await orders.SaveChangesAsync(c); return Proc(e); }
    public Task<bool> DeleteProcessAsync(int id, CancellationToken c) => Delete(orders.Processes, id, c); public async Task<IReadOnlyList<ProcessResponse>> GetProcessesAsync(string? category, CancellationToken c) { var q = orders.Processes.AsNoTracking(); if (!string.IsNullOrWhiteSpace(category)) q = q.Where(x => x.Category != null && x.Category.Name == category); return await q.OrderBy(x => x.Id).Select(x => new ProcessResponse(x.Id, x.CategoryId, x.Name, x.CreatedDate, x.ModifiedDate)).ToListAsync(c); }
    public async Task<ProcessResponse?> GetProcessAsync(int id, CancellationToken c) => await orders.Processes.AsNoTracking().Where(x => x.Id == id).Select(x => new ProcessResponse(x.Id, x.CategoryId, x.Name, x.CreatedDate, x.ModifiedDate)).SingleOrDefaultAsync(c); public async Task<bool> UpdateProcessAsync(int id, UpsertProcessRequest r, CancellationToken c) { var e = await orders.Processes.FindAsync([id], c); if (e is null) return false; e.CategoryId = r.CategoryId; e.Name = r.Name.Trim(); e.ModifiedDate = Now(); await orders.SaveChangesAsync(c); return true; }
    public async Task<OrderFileResponse?> CreateFileAsync(int orderId, string bucket, string objectName, CancellationToken c) { if (!await orders.Orders.AnyAsync(x => x.Id == orderId, c)) return null; var n = Now(); var e = new OrderFile { OrderId = orderId, Bucket = bucket.Trim(), ObjectName = objectName.Trim(), CreatedDate = n, ModifiedDate = n }; orders.Add(e); await orders.SaveChangesAsync(c); return File(e); }
    public Task<bool> DeleteFileAsync(int id, CancellationToken c) => Delete(orders.Files, id, c); public async Task<OrderFileResponse?> GetFileAsync(int id, CancellationToken c) => await ProjectFiles(orders.Files.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(c); public async Task<IReadOnlyList<OrderFileResponse>> GetFilesAsync(int orderId, CancellationToken c) => await ProjectFiles(orders.Files.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.Id)).ToListAsync(c); public async Task<bool> UpdateFileAsync(int id, UpsertOrderFileRequest r, CancellationToken c) { var e = await orders.Files.FindAsync([id], c); if (e is null) return false; e.OrderId = r.OrderId ?? e.OrderId; e.Bucket = r.Bucket.Trim(); e.ObjectName = r.ObjectName.Trim(); e.ModifiedDate = Now(); await orders.SaveChangesAsync(c); return true; }
    public async Task<OrderStatusResponse> CreateStatusAsync(UpsertOrderStatusRequest r, CancellationToken c) { var n = Now(); var e = new OrderStatus { Name = r.Name, Description = r.Description, CreatedDate = n, ModifiedDate = n }; statuses.Add(e); await statuses.SaveChangesAsync(c); return Status(e); }
    public Task<bool> DeleteStatusAsync(int id, CancellationToken c) => Delete(statuses.Statuses, id, c); public async Task<IReadOnlyList<OrderStatusResponse>> GetStatusesAsync(CancellationToken c) => await ProjectStatuses(statuses.Statuses.AsNoTracking().OrderBy(x => x.Id)).ToListAsync(c); public async Task<OrderStatusResponse?> GetStatusAsync(int id, CancellationToken c) => await ProjectStatuses(statuses.Statuses.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(c); public async Task<OrderStatusResponse?> GetStatusAsync(string name, CancellationToken c) => await ProjectStatuses(statuses.Statuses.AsNoTracking().Where(x => x.Name == name)).SingleOrDefaultAsync(c); public async Task<bool> UpdateStatusAsync(int id, UpsertOrderStatusRequest r, CancellationToken c) { var e = await statuses.Statuses.FindAsync([id], c); if (e is null) return false; e.Name = r.Name; e.Description = r.Description; e.ModifiedDate = Now(); await statuses.SaveChangesAsync(c); return true; }
    public async Task<IReadOnlyList<OrderStatusResponse>> GetAvailableStatusesAsync(int id, CancellationToken c) => await statuses.Transitions.AsNoTracking().Where(x => x.OrderStatusId == id).Select(x => new OrderStatusResponse(x.PossibleStatus!.Id, x.PossibleStatus.Name, x.PossibleStatus.Description, x.PossibleStatus.CreatedDate, x.PossibleStatus.ModifiedDate)).ToListAsync(c);
    public async Task<UpdateResult> TransitionAsync(int orderId, string name, CancellationToken c) { var id = await statuses.Statuses.Where(x => x.Name != null && x.Name.ToLower() == name.ToLower()).Select(x => (int?)x.Id).SingleOrDefaultAsync(c); return id is null ? UpdateResult.NotFound : await TransitionAsync(orderId, id.Value, c); }
    public async Task<UpdateResult> TransitionAsync(int orderId, int statusId, CancellationToken c)
    {
        var target = await statuses.Statuses
            .Where(x => x.Id == statusId)
            .Select(x => new { x.Name })
            .SingleOrDefaultAsync(c);
        if (!await orders.Orders.AnyAsync(x => x.Id == orderId, c) || target is null)
        {
            return UpdateResult.NotFound;
        }

        var strategy = statuses.Database.CreateExecutionStrategy();
        var transition = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await statuses.Database.BeginTransactionAsync(IsolationLevel.Serializable, c);
            var current = await statuses.History
                .Where(x => x.OrderId == orderId)
                .OrderByDescending(x => x.Id)
                .Select(x => (int?)x.OrderStatusId)
                .FirstOrDefaultAsync(c);
            if (current == statusId
                && string.Equals(target.Name, "Accepted", StringComparison.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(c);
                return UpdateResult.Updated;
            }

            if (current is not null
                && !await statuses.Transitions.AnyAsync(
                    x => x.OrderStatusId == current && x.PossibleStatusId == statusId,
                    c))
            {
                await tx.RollbackAsync(c);
                return UpdateResult.InvalidTransition;
            }

            var now = Now();
            statuses.Add(new OrderStatusHistory
            {
                OrderId = orderId,
                OrderStatusId = statusId,
                CreatedDate = now,
                ModifiedDate = now,
            });
            try
            {
                await statuses.SaveChangesAsync(c);
                await tx.CommitAsync(c);
                return UpdateResult.Updated;
            }
            catch (DbUpdateException)
            {
                await tx.RollbackAsync(c);
                return UpdateResult.Conflict;
            }
        });

        if (transition != UpdateResult.Updated
            || !string.Equals(target.Name, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            return transition;
        }

        return await ConvergeAcceptedCancellationAsync(orderId, c);
    }
    public Task<bool> DeleteHistoryAsync(int id, CancellationToken c) => Delete(statuses.History, id, c); public async Task<OrderStatusResponse?> GetLatestStatusAsync(int orderId, CancellationToken c) => await statuses.History.AsNoTracking().Where(x => x.OrderId == orderId).OrderByDescending(x => x.Id).Select(x => new OrderStatusResponse(x.OrderStatus!.Id, x.OrderStatus.Name, x.OrderStatus.Description, x.CreatedDate, x.ModifiedDate)).FirstOrDefaultAsync(c); public async Task<IReadOnlyList<OrderStatusHistoryResponse>> GetHistoryAsync(int orderId, CancellationToken c) => await statuses.History.AsNoTracking().Where(x => x.OrderId == orderId).OrderBy(x => x.CreatedDate).Select(x => new OrderStatusHistoryResponse(x.Id, x.OrderId, x.OrderStatusId, x.OrderStatus!.Name, x.OrderStatus.Description, x.CreatedDate, x.ModifiedDate)).ToListAsync(c); public async Task<UpdateResult> UpdateHistoryAsync(int id, UpsertOrderStatusHistoryRequest r, DateTimeOffset? expected, CancellationToken c) { var e = await statuses.History.FindAsync([id], c); if (e is null) return UpdateResult.NotFound; if (expected is not null) statuses.Entry(e).Property(x => x.ModifiedDate).OriginalValue = expected.Value.UtcDateTime; e.OrderId = r.OrderId; e.OrderStatusId = r.OrderStatusId; e.ModifiedDate = Now(); try { await statuses.SaveChangesAsync(c); return UpdateResult.Updated; } catch (DbUpdateConcurrencyException) { return UpdateResult.Conflict; } }
    private async Task<UpdateResult> ConvergeAcceptedCancellationAsync(int orderId, CancellationToken c)
    {
        try
        {
            var modifiedDate = Now();
            var changed = await orders.Orders
                .Where(x => x.Id == orderId && x.AllowCancellation)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(x => x.AllowCancellation, false)
                        .SetProperty(x => x.ModifiedDate, modifiedDate),
                    c);
            if (changed == 1)
            {
                var tracked = orders.ChangeTracker.Entries<Order>()
                    .SingleOrDefault(x => x.Entity.Id == orderId);
                if (tracked is not null)
                {
                    tracked.Property(x => x.AllowCancellation).CurrentValue = false;
                    tracked.Property(x => x.AllowCancellation).OriginalValue = false;
                    tracked.Property(x => x.ModifiedDate).CurrentValue = modifiedDate;
                    tracked.Property(x => x.ModifiedDate).OriginalValue = modifiedDate;
                }
            }

            await cache.RemoveAsync(Key(orderId), c);
            return UpdateResult.Updated;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            return UpdateResult.Conflict;
        }
    }

    private Task<bool> HasLatestStatusAsync(int orderId, string name, CancellationToken c) =>
        statuses.History
            .Where(x => x.OrderId == orderId)
            .OrderByDescending(x => x.Id)
            .Select(x => EF.Functions.ILike(x.OrderStatus!.Name!, name))
            .FirstOrDefaultAsync(c);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime; private static string Key(int id) => $"order:{id}"; private Task<OrderResponse?> ReadOrder(int id, CancellationToken c) => ProjectOrders(orders.Orders.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync(c); private static async Task<PaginatedResponse<T>?> Page<T>(IQueryable<T> q, int p, int s, CancellationToken c) { p = Math.Max(p, 1); s = Math.Clamp(s, 1, 250); var n = await q.CountAsync(c); if (n == 0) return null; return new(await q.Skip((p - 1) * s).Take(s).ToListAsync(c), p, (int)Math.Ceiling(n / (double)s), n); }
    private static Order Map(Order x, UpsertOrderRequest r) { x.CustomerId = r.CustomerId; x.EmployeeId = r.EmployeeId; x.Name = r.Name; x.Description = r.Description; x.ProcessId = r.ProcessId; x.MaterialId = r.MaterialId; x.SurfaceFinishId = r.SurfaceFinishId; x.ColorId = r.ColorId; x.Quantity = r.Quantity; x.Manufactured = r.Manufactured; x.UnitPrice = r.UnitPrice; x.DiscountPercent = r.DiscountPercent; x.CurrencyId = r.CurrencyId; x.LeadTime = r.LeadTime; x.PromisedDate = r.PromisedDate; x.FinishedDate = r.FinishedDate; x.Comment = r.Comment; x.AllowSocialMedia = r.AllowSocialMedia; x.AllowCancellation = r.AllowCancellation; x.AllowPayment = r.AllowPayment; x.TrackingNumber = r.TrackingNumber; return x; }
    private static IQueryable<OrderResponse> ProjectOrders(IQueryable<Order> q) => q.Select(x => new OrderResponse(x.Id, x.CustomerId, x.EmployeeId, x.Name, x.Description, x.ProcessId, x.MaterialId, x.SurfaceFinishId, x.ColorId, x.Quantity, x.Manufactured, x.Remaining, x.UnitPrice, x.DiscountPercent, x.Subtotal, x.CurrencyId, x.LeadTime, x.PromisedDate, x.FinishedDate, x.Turnaround, x.Comment, x.AllowSocialMedia, x.AllowCancellation, x.AllowPayment, x.TrackingNumber, x.CreatedDate, x.ModifiedDate)); private static IQueryable<OrderFileResponse> ProjectFiles(IQueryable<OrderFile> q) => q.Select(x => new OrderFileResponse(x.Id, x.OrderId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate)); private static IQueryable<OrderStatusResponse> ProjectStatuses(IQueryable<OrderStatus> q) => q.Select(x => new OrderStatusResponse(x.Id, x.Name, x.Description, x.CreatedDate, x.ModifiedDate)); private static CategoryResponse Cat(Category x) => new(x.Id, x.Name, x.CreatedDate, x.ModifiedDate); private static FileFormatResponse Format(FileFormat x) => new(x.Id, x.Name, x.Extension, x.CreatedDate, x.ModifiedDate); private static ProcessResponse Proc(Process x) => new(x.Id, x.CategoryId, x.Name, x.CreatedDate, x.ModifiedDate); private static OrderFileResponse File(OrderFile x) => new(x.Id, x.OrderId, x.Bucket, x.ObjectName, x.CreatedDate, x.ModifiedDate); private static OrderStatusResponse Status(OrderStatus x) => new(x.Id, x.Name, x.Description, x.CreatedDate, x.ModifiedDate);
    private static async Task<bool> Delete<T>(DbSet<T> s, int id, CancellationToken c) where T : class => await s.Where(x => EF.Property<int>(x, "Id") == id).ExecuteDeleteAsync(c) == 1;
}
