using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orders/[controller]"), Authorize]
public sealed class ProcessesController(IOrderService s) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.CatalogWrite), ProducesResponseType<ProcessReadResponse>(StatusCodes.Status201Created)] public async Task<IActionResult> CreateProcessAsync(UpsertProcessRequest i, CancellationToken c) { if (i is null) return BadRequest(); var v = await s.CreateProcessAsync(i, c); return CreatedAtRoute("GetProcess", new { id = v.Id }, ProcessReadResponse.FromCreated(v)); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.CatalogDelete)] public async Task<IActionResult> DeleteProcessAsync(int id, CancellationToken c) => await s.DeleteProcessAsync(id, c) ? NoContent() : NotFound();
    [HttpGet("additive"), RequirePermission(OrderPermissions.CatalogRead)] public Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> GetAdditiveProcessAsync(CancellationToken c) => List("Additive", c);
    [HttpGet, RequirePermission(OrderPermissions.CatalogRead)] public Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> GetAllProcessesAsync(CancellationToken c) => List(null, c);
    [HttpGet("electronics"), RequirePermission(OrderPermissions.CatalogRead)] public Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> GetElectronicsProcessAsync(CancellationToken c) => List("Electronics", c);
    [HttpGet("machining"), RequirePermission(OrderPermissions.CatalogRead)] public Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> GetMachiningProcessAsync(CancellationToken c) => List("Machining", c);
    [HttpGet("{id:int}", Name = "GetProcess"), RequirePermission(OrderPermissions.CatalogRead)] public async Task<ActionResult<ProcessReadResponse>> GetProcessAsync(int id, CancellationToken c) { var v = await s.GetProcessAsync(id, c); return v is null ? NotFound() : ProcessReadResponse.From(new(v, null)); }
    [HttpGet("scanning"), RequirePermission(OrderPermissions.CatalogRead)] public Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> GetScanningProcessAsync(CancellationToken c) => List("Scanning", c);
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.CatalogWrite)] public async Task<IActionResult> UpdateProcessAsync(int id, UpsertProcessRequest i, CancellationToken c) { if (i is null) return BadRequest(); return await s.UpdateProcessAsync(id, i, c) ? NoContent() : NotFound(); }
    private async Task<ActionResult<IReadOnlyList<ProcessReadResponse>>> List(string? category, CancellationToken c) { var v = await s.GetProcessReadsAsync(category, c); return v.Count == 0 ? NotFound() : Ok(v.Select(ProcessReadResponse.From).ToArray()); }
}
