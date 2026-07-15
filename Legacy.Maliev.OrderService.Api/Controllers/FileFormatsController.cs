using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orders/[controller]"), Authorize]
public sealed class FileFormatsController(IOrderService s) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.CatalogWrite, RequireLiveCheck = true)] public async Task<IActionResult> CreateFileFormatAsync(UpsertFileFormatRequest i, CancellationToken c) { var v = await s.CreateFileFormatAsync(i, c); return CreatedAtRoute("GetFileFormat", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.CatalogDelete, RequireLiveCheck = true)] public async Task<IActionResult> DeleteFileFormatAsync(int id, CancellationToken c) => await s.DeleteFileFormatAsync(id, c) ? NoContent() : NotFound();
    [HttpGet, RequirePermission(OrderPermissions.CatalogRead, RequireLiveCheck = true)] public async Task<ActionResult<IReadOnlyList<FileFormatResponse>>> GetAllFileFormatsAsync(CancellationToken c) { var v = await s.GetFileFormatsAsync(c); return v.Count == 0 ? NotFound() : Ok(v); }
    [HttpGet("{id:int}", Name = "GetFileFormat"), RequirePermission(OrderPermissions.CatalogRead, RequireLiveCheck = true)] public async Task<ActionResult<FileFormatResponse>> GetFileFormatAsync(int id, CancellationToken c) { var v = await s.GetFileFormatAsync(id, c); return v is null ? NotFound() : v; }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.CatalogWrite, RequireLiveCheck = true)] public async Task<IActionResult> UpdateFileFormatAsync(int id, UpsertFileFormatRequest i, CancellationToken c) => await s.UpdateFileFormatAsync(id, i, c) ? NoContent() : NotFound();
}
