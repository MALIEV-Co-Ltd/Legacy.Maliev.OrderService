namespace Legacy.Maliev.OrderService.Domain.Replacement;

/// <summary>Observed operational amounts; never invoices, ledger entries or carrier submissions.</summary>
public enum RecoveryFactKind { Cost, Claimed, Approved, Received, Rejected }
public sealed record ReplacementRecoveryFact(int Id, int OrderId, RecoveryFactKind Kind, decimal Amount, string Currency,
    DateOnly ObservedDate, string Description, ReplacementEvidence Evidence, string? ClaimReference, int? CorrectsFactId,
    int? QuotationId, int? InvoiceId);
