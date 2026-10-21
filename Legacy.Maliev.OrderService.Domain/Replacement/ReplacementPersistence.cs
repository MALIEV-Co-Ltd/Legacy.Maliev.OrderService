namespace Legacy.Maliev.OrderService.Domain.Replacement;

// Only the Order-owned persistence layer writes these records. No HTTP input binds entity rows.
public sealed class ReplacementCaseRow
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public ReplacementReason Reason { get; set; }
    public string OriginalsJson { get; set; } = "[]";
    public string EvidenceJson { get; set; } = "{}";
    public string CommandsJson { get; set; } = "[]";
    public int ReportedBy { get; set; }
    public DateTimeOffset ReportedAt { get; set; }
    public int Revision { get; set; }
}
public sealed class ReplacementAffectedRow
{
    public int CaseId { get; set; }
    public int OrderId { get; set; }
}
public sealed class ReplacementOperationRow
{
    public int EmployeeId { get; set; }
    public Guid OperationId { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public int CaseId { get; set; }
    public int ResultRevision { get; set; }
}
public sealed record ReplacementAffectedInput(int OrderId, int Quantity);
public sealed record ReplacementStoredCase(int Id, ReplacementCase Value);
