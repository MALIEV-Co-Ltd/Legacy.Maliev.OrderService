using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Legacy.Maliev.OrderService.Domain.Replacement;

namespace Legacy.Maliev.OrderService.Application.Replacement;

public sealed class ReplacementService(IReplacementRepository repository, IReplacementAuthority authority,
    IReplacementEvidenceVerifier evidence, ReplacementFeaturePolicy policy, TimeProvider clock)
{
    public Task<ReplacementStoredCase> GetAsync(string subject, int caseId, CancellationToken c) => Bounded(async () =>
    {
        Enabled(); var value = await repository.GetAsync(caseId, c) ?? throw new ReplacementNotFoundException();
        await Authorize(subject, value.Value.CustomerId, ReplacementPermissions.Read, Path(caseId), c);
        await OriginalScope(value, c); return value;
    }, c);

    public async Task<ReplacementLineage> LineageAsync(string subject, int caseId, CancellationToken c)
    {
        var value = await GetAsync(subject, caseId, c);
        return new(value.Id, value.Value.CustomerId, Array.AsReadOnly(value.Value.Originals.Select(x => x.OrderId).Distinct().Order().ToArray()),
            Array.AsReadOnly(value.Value.Attempts.Select(x => x.Id).ToArray()), Array.AsReadOnly(value.Value.Shipments.Select(x => x.Id).ToArray()), value.Value.Revision);
    }
    public Task<ReplacementStoredCase> OperationAsync(string subject, int customerId, Guid operationId, CancellationToken c) => Bounded(async () =>
    {
        Enabled(); Validate(customerId > 0 && operationId != Guid.Empty, "Require customer and operation identity.");
        var actor = await Authorize(subject, customerId, ReplacementPermissions.Read, $"/customers/{customerId}/replacementcases/operations/{operationId:D}", c);
        var value = await repository.GetOperationAsync(actor.EmployeeId, operationId, c) ?? throw new ReplacementNotFoundException();
        if (value.Value.CustomerId != customerId) throw new ReplacementDeniedException();
        await OriginalScope(value, c); return value;
    }, c);
    public Task<IReadOnlyList<ReplacementStoredCase>> ListAsync(string subject, int customerId, int orderId, CancellationToken c) => Bounded<IReadOnlyList<ReplacementStoredCase>>(async () =>
    {
        Enabled(); Validate(customerId > 0 && orderId > 0, "Require customer and original order.");
        await Authorize(subject, customerId, ReplacementPermissions.Read, $"/customers/{customerId}/orders/{orderId}", c);
        if (!await repository.OriginalOrderMatchesAsync(customerId, orderId, c)) throw new ReplacementDeniedException();
        var values = await repository.ListAsync(customerId, orderId, c);
        foreach (var value in values) await OriginalScope(value, c);
        return values;
    }, c);
    public Task<ReplacementStoredCase> ReportAsync(string subject, Guid operationId, ReportReplacementRequest input, CancellationToken c) => Bounded(async () =>
    {
        Enabled(); Validate(input is not null && input.CustomerId > 0 && input.Affected is { Count: > 0 and <= 100 } && input.Evidence is not null && operationId != Guid.Empty, "Require a bounded report and operation identity.");
        var actor = await Authorize(subject, input.CustomerId, ReplacementPermissions.Write, $"/customers/{input.CustomerId}/replacementcases", c);
        Validate(input.Affected.All(x => x is not null && x.OrderId > 0 && x.Quantity > 0) && Enum.IsDefined(input.Reason)
            && input.Evidence.DocumentId != Guid.Empty && input.Evidence.VersionId != Guid.Empty, "Require valid affected quantities, reason and stable evidence.");
        var ids = input.Affected.Select(x => x.OrderId).ToArray();
        Validate(ids.All(x => x > 0) && ids.Distinct().Count() == ids.Length, "Require distinct original orders.");
        foreach (var id in ids)
            if (!await repository.OriginalOrderMatchesAsync(input.CustomerId, id, c)) throw new ReplacementDeniedException();
        await evidence.RequireAsync(input.CustomerId, ids, input.Evidence, "Evidence", c);
        return await repository.CreateAsync(input.CustomerId, input.Reason, input.Affected, input.Evidence, actor.EmployeeId, operationId, clock.GetUtcNow(), c);
    }, c);
    public Task<ReplacementStoredCase> ExecuteAsync(string subject, int caseId, int expectedRevision, Guid operationId, ReplacementCommand command, CancellationToken c) => Bounded(async () =>
    {
        Enabled(); Validate(caseId > 0 && expectedRevision > 0 && operationId != Guid.Empty && command is not null, "Require case revision, command and operation identity.");
        var value = await repository.GetAsync(caseId, c) ?? throw new ReplacementNotFoundException();
        var permission = command is ApproveReplacement or RejectReplacement or AuthorizeReplacementRetry or WaiveReplacementReturn or WaiveReplacementQuantity
            || command is RecordReplacementRecoveryFact { FactKind: RecoveryFactKind.Approved or RecoveryFactKind.Received }
            ? ReplacementPermissions.Approve : ReplacementPermissions.Write;
        var actor = await Authorize(subject, value.Value.CustomerId, permission, Path(caseId), c);
        await OriginalScope(value, c);
        var originalIds = value.Value.Originals.Select(x => x.OrderId).ToArray();
        await evidence.RequireAsync(value.Value.CustomerId, originalIds, value.Value.ReportEvidence, "Evidence", c);
        if (command is ShipReplacement shipment)
        {
            var qa = value.Value.Attempts.SingleOrDefault(x => x.Id == shipment.AttemptId)?.QaEvidence;
            Validate(qa is not null, "Require completed QA evidence before shipment.");
            await evidence.RequireAsync(value.Value.CustomerId, originalIds, qa, "Evidence", c);
        }
        var reference = command switch { CompleteReplacementQa q => q.Evidence, RecordReplacementReturn r => r.Evidence, ConfirmReplacementDelivery d => d.Evidence, RecordReplacementRecoveryFact f => f.Evidence, _ => null };
        if (command is CompleteReplacementQa or RecordReplacementReturn or ConfirmReplacementDelivery or RecordReplacementRecoveryFact)
            Validate(reference is not null && reference.DocumentId != Guid.Empty && reference.VersionId != Guid.Empty, "Require stable command evidence.");
        if (reference is not null)
            await evidence.RequireAsync(value.Value.CustomerId, value.Value.Originals.Select(x => x.OrderId).ToArray(), reference, command is ConfirmReplacementDelivery ? "Acceptance" : "Evidence", c);
        return await repository.ExecuteAsync(caseId, expectedRevision, operationId, actor.EmployeeId, command, clock.GetUtcNow(), c);
    }, c);
    private async Task<ReplacementAuthorityDecision> Authorize(string subject, int customerId, string permission, string path, CancellationToken c)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new ReplacementDeniedException();
        var result = await authority.AuthorizeAsync(subject, customerId, permission, path, c);
        if (result.Outcome == ReplacementAuthorityOutcome.Denied) throw new ReplacementDeniedException();
        if (result.Outcome != ReplacementAuthorityOutcome.Allowed || result.EmployeeId <= 0 || string.IsNullOrWhiteSpace(result.TrustedTenantId)) throw new ReplacementUnavailableException();
        return result;
    }
    private async Task OriginalScope(ReplacementStoredCase value, CancellationToken c)
    {
        if (!await repository.OriginalsMatchAsync(value.Value, c)) throw new ReplacementDeniedException();
    }
    private void Enabled() { if (!policy.Enabled) throw new ReplacementUnavailableException(); }
    private static string Path(int id) => $"/replacementcases/{id}";
    private static void Validate([DoesNotReturnIf(false)] bool valid, string reason) { if (!valid) throw new ReplacementRuleException(reason); }
    private static async Task<T> Bounded<T>(Func<Task<T>> action, CancellationToken c)
    {
        try { return await action(); }
        catch (Exception e) when (e is DbException or HttpRequestException or IOException or JsonException || e.InnerException is DbException || e is OperationCanceledException && !c.IsCancellationRequested)
        { throw new ReplacementUnavailableException(); }
    }
}
