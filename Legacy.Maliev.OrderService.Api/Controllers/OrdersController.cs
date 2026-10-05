using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("[controller]"), Authorize]
public sealed class OrdersController(IOrderService s, IIdempotencyStore idem) : ControllerBase
{
    [HttpPost, RequirePermission(OrderPermissions.Create, IsCritical = true)]
    public async Task<IActionResult> CreateOrderAsync(UpsertOrderRequest i, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        if (InvalidRequest(i) is { } invalid) return invalid;

        try
        {
            var lookup = await IdempotentRequests.LookupAsync<UpsertOrderRequest, OrderResponse>(idem, User, "order", key, i, c);
            if (lookup.Conflict) return Conflict("Idempotency-Key was already used for a different request.");
            if (lookup.InProgress) return Conflict("An idempotent request with this key is already in progress.");
            if (lookup.Response is not null) return CreatedAtRoute("GetOrder", new { id = lookup.Response.Id }, lookup.Response);
            OrderResponse response;
            try
            {
                response = await s.CreateOrderAsync(i, c);
            }
            catch
            {
                await IdempotentRequests.ReleaseAfterFailureAsync(idem, lookup.Context);
                throw;
            }

            await IdempotentRequests.StoreAsync(idem, lookup.Context, response, c);
            return CreatedAtRoute("GetOrder", new { id = response.Id }, response);
        }
        catch (IdempotencyStoreUnavailableException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Idempotency protection is temporarily unavailable.");
        }
    }
    [HttpDelete("{id:int}"), RequirePermission(OrderPermissions.Delete, ResourcePathTemplate = "/orders/{id}", IsCritical = true), ServiceFilter(typeof(OrderDeletionAdmissionFilter))]
    public async Task<IActionResult> DeleteOrderAsync(int id, CancellationToken c)
    {
        try { return await s.DeleteOrderAsync(id, c) ? NoContent() : NotFound(); }
        catch (OrderDeletionUnavailableException) { return StatusCode(StatusCodes.Status503ServiceUnavailable, "Order deletion is temporarily unavailable."); }
        catch (OrderDeletionConflictException) { return Conflict("Order lifetime conflicts with its deletion receipt."); }
    }
    /// <summary>Reads one order including persisted computed totals and calendar turnaround.</summary>
    /// <remarks>Computed values are read from PostgreSQL; unfinished orders omit null turnaround.</remarks>
    /// <param name="id" example="42">Persistent order identifier.</param>
    /// <param name="c">Request cancellation token.</param>
    /// <returns>The order snapshot.</returns>
    /// <response code="200">The owned order snapshot.</response>
    /// <response code="404">The order does not exist.</response>
    [ProducesResponseType<OrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [OrderLifetimeConflict]
    [HttpGet("{id:int}", Name = "GetOrder"), RequirePermission(OrderPermissions.Read, ResourcePathTemplate = "/orders/{id}")] public async Task<ActionResult<OrderResponse>> GetOrderAsync(int id, CancellationToken c) { var v = await s.GetOrderAsync(id, c); return v is null ? NotFound() : v; }
    [OrderLifetimeConflict]
    [HttpGet("pending"), RequirePermission(OrderPermissions.Read)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedPendingOrderAsync([FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(null, true, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [OrderLifetimeConflict]
    [HttpGet, RequirePermission(OrderPermissions.Read)] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetPaginatedOrderAsync([FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(null, false, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [OrderLifetimeConflict]
    [HttpGet("customers/{customerId:int}"), RequirePermission(OrderPermissions.CustomerRead, ResourcePathTemplate = "/customers/{customerId}/orders")] public async Task<ActionResult<PaginatedResponse<OrderResponse>>> GetCustomerOrdersAsync(int customerId, [FromQuery] OrderSortType? sort, [FromQuery] string? search, [FromQuery] int? index, [FromQuery] int? size, CancellationToken c) { var v = await s.GetOrdersAsync(customerId, false, sort, search, index ?? 1, size ?? 50, c); return v is null ? NotFound() : v; }
    [OrderLifetimeConflict]
    [HttpGet("customers/{customerId:int}/{id:int}"), RequirePermission(OrderPermissions.CustomerRead, ResourcePathTemplate = "/customers/{customerId}/orders/{id}")] public async Task<ActionResult<CustomerOrderDetails>> GetCustomerOrderAsync(int customerId, int id, CancellationToken c) { var v = await s.GetCustomerOrderAsync(customerId, id, c); return v is null ? NotFound() : v; }
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPost("customers/{customerId:int}/{id:int}/cancel"), RequirePermission(OrderPermissions.CustomerCancel, ResourcePathTemplate = "/customers/{customerId}/orders/{id}", IsCritical = true)] public async Task<IActionResult> CancelCustomerOrderAsync(int customerId, int id, CancellationToken c) => (await s.CancelCustomerOrderAsync(customerId, id, c)) switch { UpdateResult.Updated => NoContent(), UpdateResult.InvalidTransition => Conflict("Order cannot be cancelled in its current state."), UpdateResult.Conflict => Conflict("Order was modified by another request."), _ => NotFound() };
    [OrderMutationUnavailable]
    [OrderLifetimeConflict]
    [HttpPut("{id:int}"), RequirePermission(OrderPermissions.Update, ResourcePathTemplate = "/orders/{id}", IsCritical = true)]
    public async Task<IActionResult> UpdateOrderAsync(int id, UpsertOrderRequest i, [FromHeader(Name = "X-Expected-Modified-Date")] DateTimeOffset? expected, CancellationToken c)
    {
        if (InvalidRequest(i) is { } invalid) return invalid;

        return (await s.UpdateOrderAsync(id, i, expected, c)) switch
        {
            UpdateResult.Updated => NoContent(),
            UpdateResult.Conflict => Conflict("Order was modified by another request."),
            _ => NotFound(),
        };
    }

    private static BadRequestObjectResult? InvalidRequest(UpsertOrderRequest request)
    {
        var results = new List<ValidationResult>();
        // MVC reads record validation metadata from constructor parameters. Direct callers of
        // this controller still need to enforce those same constraints before persistence.
        foreach (var parameter in typeof(UpsertOrderRequest).GetConstructors().Single().GetParameters())
        {
            var attributes = parameter.GetCustomAttributes<ValidationAttribute>().ToArray();
            if (attributes.Length == 0) continue;
            var value = typeof(UpsertOrderRequest).GetProperty(parameter.Name!)!.GetValue(request);
            Validator.TryValidateValue(value,
                new ValidationContext(request) { MemberName = parameter.Name }, results, attributes);
        }

        if (results.Count == 0) return null;

        var errors = results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty), (result, member) => new { member, result.ErrorMessage })
            .GroupBy(value => value.member)
            .ToDictionary(
                group => group.Key,
                group => group.Select(value => value.ErrorMessage ?? "The supplied value is invalid.").ToArray());
        return new BadRequestObjectResult(new ValidationProblemDetails(errors));
    }
}
