using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orderstatuses/[controller]"), Authorize]
public sealed class HistoriesController(IOrderService s, IIdempotencyStore idem) : ControllerBase
{
    [HttpPost("{orderId:int}/accepted"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryAcceptedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Accepted", key, c);
    [HttpPost("{orderId:int}/cancelled"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryCancelledStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Cancelled", key, c);
    [HttpPost("{orderId:int}/declined"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryDeclinedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Declined", key, c);
    [HttpPost("{orderId:int}/expired"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryExpiredStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Expired", key, c);
    [HttpPost("{orderId:int}/finished"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryFinishedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Finished", key, c);
    [HttpPost("{orderId:int}/InProgress"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryInProgressStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "InProgress", key, c);
    [HttpPost("{orderId:int}/new"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryNewStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "New", key, c);
    [HttpPost("{orderId:int}/paid"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryPaidStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Paid", key, c);
    [HttpPost("{orderId:int}/quoted"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryQuotedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Quoted", key, c);
    [HttpPost("{orderId:int}/rejected"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryRejectedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Rejected", key, c);
    [HttpPost("{orderId:int}/reopen"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReopenStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Reopen", key, c);
    [HttpPost("{orderId:int}/reviewed"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReviewedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Reviewed", key, c);
    [HttpPost("{orderId:int}/reviewing"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReviewingStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Reviewing", key, c);
    [HttpPost("{orderId:int}/shipped"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryShippedStatusAsync(int orderId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) => Named(orderId, "Shipped", key, c);
    [HttpPost("{orderId:int}/{statusId:int}"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)]
    public async Task<IActionResult> CreateOrderStatusEntryAsync(int orderId, int statusId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        try
        {
            var request = new TransitionRequest(orderId, statusId);
            var lookup = await IdempotentRequests.LookupAsync<TransitionRequest, TransitionResponse>(idem, User, "order-status", key, request, c);
            if (lookup.Conflict) return Conflict("Idempotency-Key was already used for a different request.");
            if (lookup.InProgress) return Conflict("An idempotent request with this key is already in progress.");
            if (lookup.Response is not null) return StatusCode(StatusCodes.Status201Created);
            UpdateResult result;
            try
            {
                result = await s.TransitionAsync(orderId, statusId, c);
            }
            catch
            {
                await IdempotentRequests.ReleaseAfterFailureAsync(idem, lookup.Context);
                throw;
            }

            if (result == UpdateResult.Updated)
            {
                await IdempotentRequests.StoreAsync(idem, lookup.Context, new TransitionResponse("Updated"), c);
            }
            else
            {
                await IdempotentRequests.ReleaseAsync(idem, lookup.Context, c);
            }

            return Result(result);
        }
        catch (IdempotencyStoreUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Idempotency protection is temporarily unavailable.");
        }
    }
    [HttpDelete("{historyId:int}"), RequirePermission(OrderPermissions.StatusDelete)] public async Task<IActionResult> DeleteHistoryAsync(int historyId, CancellationToken c) => await s.DeleteHistoryAsync(historyId, c) ? NoContent() : NotFound();
    [HttpGet("{orderId:int}/latest", Name = "GetLatest"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<OrderStatusResponse>> GetLatestAsync(int orderId, CancellationToken c) { var v = await s.GetLatestStatusAsync(orderId, c); return v is null ? NotFound() : v; }
    [HttpGet("{orderId:int}", Name = "GetHistory"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<IReadOnlyList<OrderStatusHistoryResponse>>> GetOrderHistoryAsync(int orderId, CancellationToken c) { var v = await s.GetHistoryAsync(orderId, c); return v.Count == 0 ? NotFound() : Ok(v); }
    [HttpPut("{historyId:int}"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public async Task<IActionResult> UpdateOrderHistoryAsync(int historyId, UpsertOrderStatusHistoryRequest i, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken c) => Result(await s.UpdateHistoryAsync(historyId, i, expected, c));
    private async Task<IActionResult> Named(int id, string name, string? key, CancellationToken c)
    {
        try
        {
            var request = new NamedTransitionRequest(id, name);
            var lookup = await IdempotentRequests.LookupAsync<NamedTransitionRequest, TransitionResponse>(idem, User, "order-status", key, request, c);
            if (lookup.Conflict) return Conflict("Idempotency-Key was already used for a different request.");
            if (lookup.InProgress) return Conflict("An idempotent request with this key is already in progress.");
            if (lookup.Response is not null) return StatusCode(StatusCodes.Status201Created);

            UpdateResult result;
            try
            {
                result = await s.TransitionAsync(id, name, c);
            }
            catch
            {
                await IdempotentRequests.ReleaseAfterFailureAsync(idem, lookup.Context);
                throw;
            }

            if (result == UpdateResult.Updated)
            {
                await IdempotentRequests.StoreAsync(idem, lookup.Context, new TransitionResponse("Updated"), c);
            }
            else
            {
                await IdempotentRequests.ReleaseAsync(idem, lookup.Context, c);
            }

            return Result(result);
        }
        catch (IdempotencyStoreUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Idempotency protection is temporarily unavailable.");
        }
    }

    private IActionResult Result(UpdateResult r) => r switch { UpdateResult.Updated => StatusCode(StatusCodes.Status201Created), UpdateResult.InvalidTransition => Conflict("Order status transition is not permitted."), UpdateResult.Conflict => Conflict("Concurrent status transition detected."), _ => NotFound() };
    private sealed record TransitionRequest(int OrderId, int StatusId);
    private sealed record NamedTransitionRequest(int OrderId, string StatusName);
    private sealed record TransitionResponse(string Result);
}
