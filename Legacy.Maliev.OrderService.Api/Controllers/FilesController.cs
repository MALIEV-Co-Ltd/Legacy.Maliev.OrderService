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
    /// <summary>Creates bucket and object metadata for an existing order without uploading an object.</summary>
    /// <remarks>Only the storage identity is persisted. Object transfer and storage credentials are not part of this request.</remarks>
    /// <param name="orderId" example="42">Owner order identifier.</param>
    /// <param name="bucket" example="metadata-bucket">Nonblank storage bucket name.</param>
    /// <param name="objectName" example="drawings/part.step">Nonblank object name; URI-encode query values.</param>
    /// <param name="c">Request cancellation token.</param>
    /// <returns>The persisted metadata record and its Location.</returns>
    /// <response code="201">The metadata record and its read Location.</response>
    /// <response code="400">Bucket or object name is blank.</response>
    /// <response code="404">The owner order does not exist.</response>
    [ProducesResponseType<OrderFileResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPost("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesWrite, ResourcePathTemplate = "/orders/{orderId}")] public async Task<IActionResult> CreateOrderFileEntryAsync(int orderId, [FromQuery] string bucket, [FromQuery] string objectName, CancellationToken c) { if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectName)) return BadRequest(); var v = await s.CreateFileAsync(orderId, bucket, objectName, c); return v is null ? NotFound() : CreatedAtRoute("GetOrderFile", new { id = v.Id }, v); }
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.FilesDelete)] public async Task<IActionResult> DeleteOrderFileAsync(int id, CancellationToken c) => await s.DeleteFileAsync(id, c) ? NoContent() : NotFound();
    [OrderLifetimeConflict]
    [HttpGet("{id:int}", Name = "GetOrderFile"), RequirePermission(OrderPermissions.FilesRead)] public async Task<ActionResult<OrderFileResponse>> GetOrderFileAsync(int id, CancellationToken c) { var v = await s.GetFileAsync(id, c); return v is null ? NotFound() : v; }
    [OrderLifetimeConflict]
    [HttpGet("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesRead, ResourcePathTemplate = "/orders/{orderId}")] public async Task<ActionResult<IReadOnlyList<OrderFileResponse>>> GetOrderFilesAsync(int orderId, CancellationToken c) { var v = await s.GetFilesAsync(orderId, c); return v.Count == 0 ? NotFound() : Ok(v); }
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.FilesWrite)] public async Task<IActionResult> UpdateOrderFileAsync(int id, UpsertOrderFileRequest i, CancellationToken c) => await s.UpdateFileAsync(id, i, c) ? NoContent() : NotFound();
}
