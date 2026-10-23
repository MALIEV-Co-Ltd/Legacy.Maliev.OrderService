using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class OrderStatusesController(IOrderService s) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.StatusWrite)] public async Task<IActionResult> CreateOrderStatusAsync(UpsertOrderStatusRequest i, CancellationToken c) { if (i is null) return BadRequest(); var v = await s.CreateStatusAsync(i, c); return CreatedAtRoute("GetOrderStatus", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.StatusDelete)] public async Task<IActionResult> DeleteOrderStatusAsync(int id, CancellationToken c) => await s.DeleteStatusAsync(id, c) ? NoContent() : NotFound();
    [HttpGet, RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<IReadOnlyList<OrderStatusReadResponse>>> GetAllOrderStatusesAsync(CancellationToken c) { var v = await s.GetStatusesAsync(c); return v.Count == 0 ? NotFound() : Ok(v.Select(OrderStatusReadResponse.From).ToArray()); }
    [HttpGet("{id:int}", Name = "GetOrderStatus"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<OrderStatusReadResponse>> GetOrderStatusAsync(int id, CancellationToken c) { var v = await s.GetStatusAsync(id, c); return v is null ? NotFound() : OrderStatusReadResponse.From(v); }
    [HttpGet("{statusName}", Name = "GetOrderStatusName"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<OrderStatusReadResponse>> GetOrderStatusByNameAsync(string statusName, CancellationToken c) { var v = await s.GetStatusAsync(statusName, c); return v is null ? NotFound() : OrderStatusReadResponse.From(v); }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.StatusWrite)] public async Task<IActionResult> UpdateOrderStatusAsync(int id, UpsertOrderStatusRequest i, CancellationToken c) { if (i is null) return BadRequest(); return await s.UpdateStatusAsync(id, i, c) ? NoContent() : NotFound(); }
}
