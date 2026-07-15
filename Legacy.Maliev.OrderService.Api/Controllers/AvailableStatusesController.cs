using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orderstatuses/[controller]"), Authorize]
public sealed class AvailableStatusesController(IOrderService s) : ControllerBase
{
    [HttpGet("/orderstatuses/{currentStatusId:int}/available", Name = "GetAvailableStatus"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<IReadOnlyList<OrderStatusResponse>>> GetAvailableStatusAsync(int currentStatusId, CancellationToken c) { var v = await s.GetAvailableStatusesAsync(currentStatusId, c); return v.Count == 0 ? NotFound() : Ok(v); }
}
