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
    [HttpPost, RequirePermission(OrderPermissions.StatusWrite, RequireLiveCheck = true)] public async Task<IActionResult> CreateOrderStatusAsync(UpsertOrderStatusRequest i, CancellationToken c) { var v = await s.CreateStatusAsync(i, c); return CreatedAtRoute("GetOrderStatus", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.StatusDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteOrderStatusAsync(int id, CancellationToken c) => await s.DeleteStatusAsync(id, c) ? NoContent() : NotFound();
    [HttpGet, RequirePermission(OrderPermissions.StatusRead, RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<OrderStatusResponse>>> GetAllOrderStatusesAsync(CancellationToken c) { var v = await s.GetStatusesAsync(c); return v.Count == 0 ? NotFound() : Ok(v); }
    [HttpGet("{id:int}", Name = "GetOrderStatus"), RequirePermission(OrderPermissions.StatusRead, RequireLiveCheck = true)] public async Task<ActionResult<OrderStatusResponse>> GetOrderStatusAsync(int id, CancellationToken c) { var v = await s.GetStatusAsync(id, c); return v is null ? NotFound() : v; }
    [HttpGet("{statusName}", Name = "GetOrderStatusName"), RequirePermission(OrderPermissions.StatusRead, RequireLiveCheck = true)] public async Task<ActionResult<OrderStatusResponse>> GetOrderStatusByNameAsync(string statusName, CancellationToken c) { var v = await s.GetStatusAsync(statusName, c); return v is null ? NotFound() : v; }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.StatusWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateOrderStatusAsync(int id, UpsertOrderStatusRequest i, CancellationToken c) => await s.UpdateStatusAsync(id, i, c) ? NoContent() : NotFound();
}
