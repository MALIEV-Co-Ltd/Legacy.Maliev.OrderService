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
    [HttpPost("{orderId:int}/accepted"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryAcceptedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Accepted", c);
    [HttpPost("{orderId:int}/cancelled"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryCancelledStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Cancelled", c);
    [HttpPost("{orderId:int}/declined"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryDeclinedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Declined", c);
    [HttpPost("{orderId:int}/expired"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryExpiredStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Expired", c);
    [HttpPost("{orderId:int}/finished"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryFinishedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Finished", c);
    [HttpPost("{orderId:int}/InProgress"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryInProgressStatusAsync(int orderId, CancellationToken c) => Named(orderId, "InProgress", c);
    [HttpPost("{orderId:int}/new"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryNewStatusAsync(int orderId, CancellationToken c) => Named(orderId, "New", c);
    [HttpPost("{orderId:int}/paid"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryPaidStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Paid", c);
    [HttpPost("{orderId:int}/quoted"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryQuotedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Quoted", c);
    [HttpPost("{orderId:int}/rejected"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryRejectedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Rejected", c);
    [HttpPost("{orderId:int}/reopen"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReopenStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Reopen", c);
    [HttpPost("{orderId:int}/reviewed"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReviewedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Reviewed", c);
    [HttpPost("{orderId:int}/reviewing"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryReviewingStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Reviewing", c);
    [HttpPost("{orderId:int}/shipped"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public Task<IActionResult> CreateOrderHistoryShippedStatusAsync(int orderId, CancellationToken c) => Named(orderId, "Shipped", c);
    [HttpPost("{orderId:int}/{statusId:int}"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public async Task<IActionResult> CreateOrderStatusEntryAsync(int orderId, int statusId, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) { if (!string.IsNullOrWhiteSpace(key) && await idem.GetAsync<TransitionResponse>("order-status", key, c) is not null) return StatusCode(StatusCodes.Status201Created); var result = await s.TransitionAsync(orderId, statusId, c); if (result == UpdateResult.Updated && !string.IsNullOrWhiteSpace(key)) await idem.SetAsync("order-status", key, new TransitionResponse("Updated"), c); return Result(result); }
    [HttpDelete("{historyId:int}"), RequirePermission(OrderPermissions.StatusDelete)] public async Task<IActionResult> DeleteHistoryAsync(int historyId, CancellationToken c) => await s.DeleteHistoryAsync(historyId, c) ? NoContent() : NotFound();
    [HttpGet("{orderId:int}/latest", Name = "GetLatest"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<OrderStatusResponse>> GetLatestAsync(int orderId, CancellationToken c) { var v = await s.GetLatestStatusAsync(orderId, c); return v is null ? NotFound() : v; }
    [HttpGet("{orderId:int}", Name = "GetHistory"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<IReadOnlyList<OrderStatusHistoryResponse>>> GetOrderHistoryAsync(int orderId, CancellationToken c) { var v = await s.GetHistoryAsync(orderId, c); return v.Count == 0 ? NotFound() : Ok(v); }
    [HttpPut("{historyId:int}"), RequirePermission(OrderPermissions.StatusWrite, IsCritical = true)] public async Task<IActionResult> UpdateOrderHistoryAsync(int historyId, UpsertOrderStatusHistoryRequest i, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken c) => Result(await s.UpdateHistoryAsync(historyId, i, expected, c));
    private async Task<IActionResult> Named(int id, string name, CancellationToken c) => Result(await s.TransitionAsync(id, name, c)); private IActionResult Result(UpdateResult r) => r switch { UpdateResult.Updated => StatusCode(StatusCodes.Status201Created), UpdateResult.InvalidTransition => Conflict("Order status transition is not permitted."), UpdateResult.Conflict => Conflict("Concurrent status transition detected."), _ => NotFound() }; private sealed record TransitionResponse(string Result);
}
