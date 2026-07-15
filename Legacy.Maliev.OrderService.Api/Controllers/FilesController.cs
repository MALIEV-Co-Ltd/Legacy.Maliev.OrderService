using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orders/[controller]"), Authorize]
public sealed class FilesController(IOrderService s) : ControllerBase
{
    [HttpPost("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesWrite, ResourcePathTemplate = "/orders/{orderId}")] public async Task<IActionResult> CreateOrderFileEntryAsync(int orderId, [FromQuery] string bucket, [FromQuery] string objectName, CancellationToken c) { if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectName)) return BadRequest(); var v = await s.CreateFileAsync(orderId, bucket, objectName, c); return v is null ? NotFound() : CreatedAtRoute("GetOrderFile", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.FilesDelete)] public async Task<IActionResult> DeleteOrderFileAsync(int id, CancellationToken c) => await s.DeleteFileAsync(id, c) ? NoContent() : NotFound();
    [HttpGet("{id:int}", Name = "GetOrderFile"), RequirePermission(OrderPermissions.FilesRead)] public async Task<ActionResult<OrderFileResponse>> GetOrderFileAsync(int id, CancellationToken c) { var v = await s.GetFileAsync(id, c); return v is null ? NotFound() : v; }
    [HttpGet("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesRead, ResourcePathTemplate = "/orders/{orderId}")] public async Task<ActionResult<IReadOnlyList<OrderFileResponse>>> GetOrderFilesAsync(int orderId, CancellationToken c) { var v = await s.GetFilesAsync(orderId, c); return v.Count == 0 ? NotFound() : Ok(v); }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.FilesWrite)] public async Task<IActionResult> UpdateOrderFileAsync(int id, UpsertOrderFileRequest i, CancellationToken c) => await s.UpdateFileAsync(id, i, c) ? NoContent() : NotFound();
}
