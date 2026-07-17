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
    [HttpPost, RequirePermission(OrderPermissions.Create, IsCritical = true)]
    public async Task<IActionResult> CreateOrderAsync(UpsertOrderRequest i, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        try
        {
            var lookup = await IdempotentRequests.LookupAsync<UpsertOrderRequest, OrderResponse>(idem, User, "order", key, i, c);
            if (lookup.Conflict) return Conflict("Idempotency-Key was already used for a different request.");
            if (lookup.InProgress) return Conflict("An idempotent request with this key is already in progress.");
            if (lookup.Response is not null) return CreatedAtRoute("GetOrder", new { id = lookup.Response.Id }, lookup.Response);
            OrderResponse response;
            try
            {
                response = await s.CreateOrderAsync(i, c);
            }
            catch
            {
                await IdempotentRequests.ReleaseAfterFailureAsync(idem, lookup.Context);
                throw;
            }

            await IdempotentRequests.StoreAsync(idem, lookup.Context, response, c);
            return CreatedAtRoute("GetOrder", new { id = response.Id }, response);
        }
        catch (IdempotencyStoreUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Idempotency protection is temporarily unavailable.");
        }
    }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.Delete, ResourcePathTemplate = "/orders/{id}", IsCritical = true)] public async Task<IActionResult> DeleteOrderAsync(int id, CancellationToken c) => await s.DeleteOrderAsync(id, c) ? NoContent() : NotFound();
    [HttpGet("{id:int}", Name = "GetOrder"), RequirePermission(OrderPermissions.Read, ResourcePathTemplate = "/orders/{id}")] public async Task<ActionResult<OrderResponse>> GetOrderAsync(int id, CancellationToken c) { var v = await s.GetOrderAsync(id, c); return v is null ? NotFound() : v; }
    [HttpGet("pending"), RequirePermission(OrderPermissions.Read)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedPendingOrderAsync([FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(null, true, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [HttpGet, RequirePermission(OrderPermissions.Read)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedOrderAsync([FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(null, false, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [HttpGet("customers/{customerId:int}"), RequirePermission(OrderPermissions.CustomerRead, ResourcePathTemplate = "/customers/{customerId}/orders")] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetCustomerOrdersAsync(int customerId, [FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(customerId, false, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [HttpGet("customers/{customerId:int}/{id:int}"), RequirePermission(OrderPermissions.CustomerRead, ResourcePathTemplate = "/customers/{customerId}/orders/{id}")] public async Task<ActionResult<CustomerOrderDetails>> GetCustomerOrderAsync(int customerId, int id, CancellationToken c) { var v = await s.GetCustomerOrderAsync(customerId, id, c); return v is null ? NotFound() : v; }
    [HttpPost("customers/{customerId:int}/{id:int}/cancel"), RequirePermission(OrderPermissions.CustomerCancel, ResourcePathTemplate = "/customers/{customerId}/orders/{id}", IsCritical = true)] public async Task<IActionResult> CancelCustomerOrderAsync(int customerId, int id, CancellationToken c) => (await s.CancelCustomerOrderAsync(customerId, id, c)) switch { UpdateResult.Updated => NoContent(), UpdateResult.InvalidTransition => Conflict("Order cannot be cancelled in its current state."), UpdateResult.Conflict => Conflict("Order was modified by another request."), _ => NotFound() };
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.Update, ResourcePathTemplate = "/orders/{id}", IsCritical = true)] public async Task<IActionResult> UpdateOrderAsync(int id, UpsertOrderRequest i, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken c) => (await s.UpdateOrderAsync(id, i, expected, c)) switch { UpdateResult.Updated => NoContent(), UpdateResult.Conflict => Conflict("Order was modified by another request."), _ => NotFound() };
}
