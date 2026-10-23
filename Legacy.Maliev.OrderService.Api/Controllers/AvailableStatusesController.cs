using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orderstatuses/[controller]"), Authorize]
public sealed class AvailableStatusesController(IOrderService s) : ControllerBase
{
    [HttpGet("/orderstatuses/{currentStatusId:int}/available", Name = "GetAvailableStatus"), RequirePermission(OrderPermissions.StatusRead)] public async Task<ActionResult<IReadOnlyList<OrderStatusReadResponse>>> GetAvailableStatusAsync(int currentStatusId, CancellationToken c) { var v = await s.GetAvailableStatusesAsync(currentStatusId, c); return v.Count == 0 ? NotFound() : Ok(v.Select(OrderStatusReadResponse.From).ToArray()); }
}
