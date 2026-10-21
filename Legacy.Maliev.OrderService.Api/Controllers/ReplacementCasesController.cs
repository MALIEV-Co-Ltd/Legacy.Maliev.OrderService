using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Replacement;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.OrderService.Api.Controllers;

[ApiController, Route("replacementcases"), Authorize, RequestSizeLimit(65536)]
public sealed class ReplacementCasesController(ReplacementService service, IIdempotencyStore idempotency) : ControllerBase
{
    private static readonly JsonSerializerOptions Wire = CreateWire();
    [HttpGet("{caseId:int}", Name = "GetReplacementCase"), RequirePermission(ReplacementPermissions.Read, ResourcePathTemplate = "/replacementcases/{caseId}", RequireLiveCheck = true)]
    public Task<IActionResult> GetAsync(int caseId, CancellationToken c) => Read(() => service.GetAsync(Subject(), caseId, c));
    [HttpGet("{caseId:int}/lineage"), RequirePermission(ReplacementPermissions.Read, ResourcePathTemplate = "/replacementcases/{caseId}", RequireLiveCheck = true)]
    public Task<IActionResult> LineageAsync(int caseId, CancellationToken c) => Read(() => service.LineageAsync(Subject(), caseId, c));
    [HttpGet("customers/{customerId:int}/orders/{orderId:int}"), RequirePermission(ReplacementPermissions.Read, ResourcePathTemplate = "/customers/{customerId}/orders/{orderId}", RequireLiveCheck = true)]
    public Task<IActionResult> ListAsync(int customerId, int orderId, CancellationToken c) => Read(() => service.ListAsync(Subject(), customerId, orderId, c));
    [HttpGet("customers/{customerId:int}/operations/{operationId:guid}"), RequirePermission(ReplacementPermissions.Read, ResourcePathTemplate = "/customers/{customerId}/replacementcases/operations/{operationId}", RequireLiveCheck = true)]
    public Task<IActionResult> OperationAsync(int customerId, Guid operationId, CancellationToken c) => Read(() => service.OperationAsync(Subject(), customerId, operationId, c));
    [HttpPost("customers/{customerId:int}"), RequirePermission(ReplacementPermissions.Write, ResourcePathTemplate = "/customers/{customerId}/replacementcases", RequireLiveCheck = true, IsCritical = true)]
    public Task<IActionResult> ReportAsync(int customerId, ReportReplacementRequest input, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        if (input.CustomerId != customerId) return Task.FromResult<IActionResult>(BadRequest("Customer must match route."));
        return Write($"replacement-report:{customerId}", key, input, op => service.ReportAsync(Subject(), op, input, c), true, c);
    }
    [HttpPost("{caseId:int}/commands"), RequirePermission(ReplacementPermissions.Write, ResourcePathTemplate = "/replacementcases/{caseId}", RequireLiveCheck = true, IsCritical = true)]
    public Task<IActionResult> CommandAsync(int caseId, ReplacementCommandRequest input, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        if (IsDecision(input.Command)) return Task.FromResult<IActionResult>(BadRequest("Use the approval decision route."));
        return Write($"replacement-command:{caseId}", key, input, op => service.ExecuteAsync(Subject(), caseId, input.ExpectedRevision, op, input.Command, c), false, c);
    }
    [HttpPost("{caseId:int}/decisions"), RequirePermission(ReplacementPermissions.Approve, ResourcePathTemplate = "/replacementcases/{caseId}", RequireLiveCheck = true, IsCritical = true)]
    public Task<IActionResult> DecisionAsync(int caseId, ReplacementCommandRequest input, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken c)
    {
        if (!IsDecision(input.Command)) return Task.FromResult<IActionResult>(BadRequest("Require an approval decision."));
        return Write($"replacement-decision:{caseId}", key, input, op => service.ExecuteAsync(Subject(), caseId, input.ExpectedRevision, op, input.Command, c), false, c);
    }
    private async Task<IActionResult> Write<T>(string scope, string? key, T request, Func<Guid, Task<ReplacementStoredCase>> execute, bool created, CancellationToken c)
    {
        if (!Guid.TryParseExact(key, "D", out var operation) || operation == Guid.Empty) return BadRequest("Require a stable GUID Idempotency-Key.");
        try
        {
            var lookup = await IdempotentRequests.LookupAsync<T, ReplacementOperationMarker>(idempotency, User, scope, key, request, c);
            if (lookup.Conflict || lookup.InProgress) return Conflict("Operation payload conflicts or is still in progress. Reconcile the same operation.");
            ReplacementStoredCase value;
            try
            {
                // Redis replay never bypasses current authority/evidence. Durable actor-scoped receipts
                // provide the exact historical result without repeating physical production or shipping.
                value = await execute(operation);
            }
            catch { await IdempotentRequests.ReleaseAfterFailureAsync(idempotency, lookup.Context); throw; }
            if (lookup.Response is null) await IdempotentRequests.StoreAsync(idempotency, lookup.Context, new ReplacementOperationMarker(value.Id, value.Value.Revision), c);
            var result = new JsonResult(value, Wire) { StatusCode = created ? 201 : 200 };
            if (created) Response.Headers.Location = Url.RouteUrl("GetReplacementCase", new { caseId = value.Id });
            return result;
        }
        catch (Exception e) when (Known(e)) { return Failure(e); }
    }
    public sealed record ReplacementOperationMarker(int CaseId, int Revision);
    private async Task<IActionResult> Read<T>(Func<Task<T>> action)
    {
        try { return new JsonResult(await action(), Wire); }
        catch (Exception e) when (Known(e)) { return Failure(e); }
    }
    private IActionResult Failure(Exception e) => e switch
    {
        ReplacementDeniedException => StatusCode(403),
        ReplacementNotFoundException => NotFound(),
        ReplacementConflictException { RevisionRejected: true } => RejectedRevision(),
        ReplacementConflictException => Conflict("Case or operation changed."),
        ReplacementRuleException => BadRequest(e.Message),
        _ => StatusCode(503, "Replacement authority or storage is unavailable.")
    };
    private IActionResult RejectedRevision()
    {
        // The durable operation fence found no receipt before rejecting the stale revision.
        // This header never applies to an in-progress or mismatched operation identity.
        Response.Headers["X-Replacement-Rejection"] = "Revision"; return Conflict("Case revision changed; no command was recorded for this operation.");
    }
    private static bool Known(Exception e) => e is ReplacementDeniedException or ReplacementNotFoundException or ReplacementConflictException or ReplacementRuleException or ReplacementUnavailableException or IdempotencyStoreUnavailableException;
    private string Subject() => User.FindFirst("user_id")?.Value ?? User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    private static bool IsDecision(ReplacementCommand? c) => c is ApproveReplacement or RejectReplacement or AuthorizeReplacementRetry or WaiveReplacementReturn or WaiveReplacementQuantity || c is RecordReplacementRecoveryFact { FactKind: RecoveryFactKind.Approved or RecoveryFactKind.Received };
    private static JsonSerializerOptions CreateWire() { var options = new JsonSerializerOptions { PropertyNamingPolicy = null }; options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); return options; }
}
