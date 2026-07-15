using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("orders/[controller]"), Authorize]
public sealed class CategoriesController(IOrderService s) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.CatalogWrite)] public async Task<IActionResult> CreateCategoryAsync(UpsertCategoryRequest i, CancellationToken c) { var v = await s.CreateCategoryAsync(i, c); return CreatedAtRoute("GetCategory", new { id = v.Id }, v); }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.CatalogDelete)] public async Task<IActionResult> DeleteCategoryAsync(int id, CancellationToken c) => await s.DeleteCategoryAsync(id, c) ? NoContent() : NotFound();
    [HttpGet("{id:int}", Name = "GetCategory"), RequirePermission(OrderPermissions.CatalogRead)] public async Task<ActionResult<CategoryResponse>> GetCategoryAsync(int id, CancellationToken c) { var v = await s.GetCategoryAsync(id, c); return v is null ? NotFound() : v; }
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.CatalogWrite)] public async Task<IActionResult> UpdateCategoryAsync(int id, UpsertCategoryRequest i, CancellationToken c) => await s.UpdateCategoryAsync(id, i, c) ? NoContent() : NotFound();
}
