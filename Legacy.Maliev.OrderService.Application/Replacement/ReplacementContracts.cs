using Legacy.Maliev.OrderService.Domain.Replacement;

namespace Legacy.Maliev.OrderService.Application.Replacement;

public static class ReplacementPermissions
{
    public const string Read = "legacy.replacements.read";
    public const string Write = "legacy.replacements.write";
    public const string Approve = "legacy.replacements.approve";
}
public sealed record ReplacementFeaturePolicy(bool Enabled = false);
public enum ReplacementAuthorityOutcome { Allowed, Denied, Unavailable }
// Only an accepted owner adapter may produce Allowed after current session, active employee,
// trusted member tenant/customer and exact resource permission checks. JWT display claims are insufficient.
public sealed record ReplacementAuthorityDecision(ReplacementAuthorityOutcome Outcome, int EmployeeId = 0, string? TrustedTenantId = null);
public interface IReplacementAuthority
{
    Task<ReplacementAuthorityDecision> AuthorizeAsync(string subject, int customerId, string permission, string resourcePath, CancellationToken cancellationToken);
}
public interface IReplacementEvidenceVerifier
{
    Task RequireAsync(int customerId, IReadOnlyList<int> originalOrderIds, ReplacementEvidence reference, string requiredKind, CancellationToken cancellationToken);
}
public interface IReplacementRepository
{
    Task<ReplacementStoredCase> CreateAsync(int customerId, ReplacementReason reason, IReadOnlyList<ReplacementAffectedInput> affected, ReplacementEvidence evidence, int employeeId, Guid operationId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ReplacementStoredCase> ExecuteAsync(int caseId, int expectedRevision, Guid operationId, int employeeId, ReplacementCommand command, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ReplacementStoredCase?> GetAsync(int caseId, CancellationToken cancellationToken);
    Task<ReplacementStoredCase?> GetOperationAsync(int employeeId, Guid operationId, CancellationToken cancellationToken);
    Task<bool> OriginalsMatchAsync(ReplacementCase value, CancellationToken cancellationToken);
    Task<bool> OriginalOrderMatchesAsync(int customerId, int orderId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ReplacementStoredCase>> ListAsync(int customerId, int orderId, CancellationToken cancellationToken);
}
public sealed record ReplacementLineage(int CaseId, int CustomerId, IReadOnlyList<int> OriginalOrderIds, IReadOnlyList<int> AttemptIds, IReadOnlyList<int> ShipmentIds, int Revision);
public sealed record ReportReplacementRequest(int CustomerId, ReplacementReason Reason, IReadOnlyList<ReplacementAffectedInput> Affected, ReplacementEvidence Evidence);
public sealed record ReplacementCommandRequest(int ExpectedRevision, ReplacementCommand Command);
public sealed class ReplacementDeniedException : Exception;
public class ReplacementUnavailableException : Exception;
public class ReplacementNotFoundException : Exception;
public class ReplacementConflictException(string message, bool revisionRejected = false) : Exception(message)
{
    public bool RevisionRejected { get; } = revisionRejected;
}
