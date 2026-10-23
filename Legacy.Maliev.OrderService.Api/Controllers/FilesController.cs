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
    [ProducesResponseType<OrderFileReadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPost("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesWrite, ResourcePathTemplate = "/orders/{orderId}")] public async Task<IActionResult> CreateOrderFileEntryAsync(int orderId, [FromQuery] string bucket, [FromQuery] string objectName, CancellationToken c) { if (string.IsNullOrEmpty(bucket)) return BadRequest("bucket is required"); if (string.IsNullOrEmpty(objectName)) return BadRequest("objectName is required"); var v = await s.CreateFileAsync(orderId, bucket, objectName, c); return v is null ? NotFound("Order not found") : CreatedAtRoute("GetOrderFile", new { id = v.Id }, OrderFileReadResponse.FromCreated(v)); }
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.FilesDelete)] public async Task<IActionResult> DeleteOrderFileAsync(int id, CancellationToken c) => await s.DeleteFileAsync(id, c) ? NoContent() : NotFound();
    [OrderLifetimeConflict]
    [HttpGet("{id:int}", Name = "GetOrderFile"), RequirePermission(OrderPermissions.FilesRead)] public async Task<ActionResult<OrderFileReadResponse>> GetOrderFileAsync(int id, CancellationToken c) { var v = await s.GetFileAsync(id, c); return v is null ? NotFound() : OrderFileReadResponse.From(v); }
    [OrderLifetimeConflict]
    [HttpGet("/orders/{orderId:int}/files"), RequirePermission(OrderPermissions.FilesRead, ResourcePathTemplate = "/orders/{orderId}")] public async Task<ActionResult<IReadOnlyList<OrderFileReadResponse>>> GetOrderFilesAsync(int orderId, CancellationToken c) { var v = await s.GetFilesAsync(orderId, c); return v.Count == 0 ? NotFound() : Ok(v.Select(OrderFileReadResponse.From).ToArray()); }
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.FilesWrite)] public async Task<IActionResult> UpdateOrderFileAsync(int id, UpsertOrderFileRequest i, CancellationToken c) { if (i is null) return BadRequest("item is required."); return await s.UpdateFileAsync(id, i, c) ? NoContent() : NotFound(); }
}
