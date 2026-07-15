using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class OrdersController(IOrderService s, IIdempotencyStore idem) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.Create, RequireLiveCheck = true, IsCritical = true)] public async Task<IActionResult> CreateOrderAsync(UpsertOrderRequest i, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c) { var v = await IdempotentCreates.GetOrCreateAsync(idem, "order", key, () => s.CreateOrderAsync(i, c), c); return CreatedAtRoute("GetOrder", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.Delete, ResourcePathTemplate = "/orders/{id}", RequireLiveCheck = true, IsCritical = true)] public async Task<IActionResult> DeleteOrderAsync(int id, CancellationToken c) => await s.DeleteOrderAsync(id, c) ? NoContent() : NotFound();
    [HttpGet("{id:int}", Name = "GetOrder"), RequirePermission(OrderPermissions.Read, ResourcePathTemplate = "/orders/{id}", RequireLiveCheck = true)] public async Task<ActionResult<OrderResponse>> GetOrderAsync(int id, CancellationToken c) { var v = await s.GetOrderAsync(id, c); return v is null ? NotFound() : v; }
    [HttpGet("pending"), RequirePermission(OrderPermissions.Read, RequireLiveCheck = true)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedPendingOrderAsync([FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(null, true, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [HttpGet, HttpGet("customers/{customerId:int}"), RequirePermission(OrderPermissions.Read, RequireLiveCheck = true)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedOrderAsync(int? customerId, [FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(customerId, false, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.Update, ResourcePathTemplate = "/orders/{id}", RequireLiveCheck = true, IsCritical = true)] public async Task<IActionResult> UpdateOrderAsync(int id, UpsertOrderRequest i, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken c) => (await s.UpdateOrderAsync(id, i, expected, c)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Order was modified by another request."), _ => NotFound() };
}
