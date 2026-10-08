namespace Legacy.Maliev.OrderService.Domain.Replacement;

public enum ReplacementReason { CarrierDamage, ManufacturingNonconformance }
public enum ReplacementState { Reported, Approved, Rejected, Closed }
public enum ReturnDecision { Required, Waived }
public enum ShipmentOutcome { InTransit, Delivered, Failed }
public sealed class ReplacementRuleException(string message) : Exception(message);
public sealed record ReplacementEvidence(Guid DocumentId, Guid VersionId);
public sealed record ReplacementOriginal(int OrderId, int CustomerId, int Quantity, int Manufactured, int AffectedQuantity,
    string? TrackingNumber, DateOnly? FinishedDate, int? QuotationId, int? InvoiceId);
public sealed record ReplacementAttempt(int Id, int OrderId, int Quantity, DateTimeOffset StartedAt,
    int? ProducedQuantity = null, int AcceptedQuantity = 0, int RejectedQuantity = 0, DateOnly? FinishedDate = null,
    ReplacementEvidence? QaEvidence = null);
public sealed record ReplacementShipment(int Id, int AttemptId, int OrderId, int Quantity, string Carrier,
    string TrackingNumber, string DestinationSnapshot, DateOnly ShippedDate, ShipmentOutcome Outcome = ShipmentOutcome.InTransit,
    ReplacementEvidence? DeliveryEvidence = null, bool RetryAuthorized = false);
public sealed record ReplacementReturn(int OrderId, int Quantity, ReplacementEvidence Evidence, string Disposition);
public sealed record ReplacementWaiver(int OrderId, int Quantity, string Reason);
public sealed record ReplacementAudit(int Revision, string Action, int EmployeeId, DateTimeOffset OccurredAt, string Reason);

/// <summary>Immutable physical-remedy state. Commands never write original orders or financial records.
/// Evidence references must be verified by the shared protected-document consumer before committing a command.</summary>
public sealed class ReplacementCase
{
    public int CustomerId { get; }
    public ReplacementReason Reason { get; }
    public ReplacementState State { get; }
    public int Revision { get; }
    public ReplacementEvidence ReportEvidence { get; }
    public IReadOnlyList<ReplacementOriginal> Originals { get; }
    public IReadOnlyList<ReplacementAttempt> Attempts { get; }
    public IReadOnlyList<ReplacementShipment> Shipments { get; }
    public IReadOnlyList<ReplacementReturn> Returns { get; }
    public IReadOnlyList<ReplacementWaiver> Waivers { get; }
    public IReadOnlyList<ReplacementAudit> Audit { get; }
    public IReadOnlyList<ReplacementRecoveryFact> RecoveryFacts { get; }
    public ReturnDecision? ReturnDecision { get; }
    public bool BlockProductionUntilReturn { get; }
    public bool BlockShipmentUntilReturn { get; }

    private ReplacementCase(int customerId, ReplacementReason reason, ReplacementState state, ReplacementEvidence report,
        IEnumerable<ReplacementOriginal> originals, IEnumerable<ReplacementAttempt> attempts,
        IEnumerable<ReplacementShipment> shipments, IEnumerable<ReplacementReturn> returns,
        IEnumerable<ReplacementAudit> audit, ReturnDecision? returnDecision, bool blockProduction, bool blockShipment,
        IEnumerable<ReplacementWaiver>? waivers = null, IEnumerable<ReplacementRecoveryFact>? recoveryFacts = null)
    {
        CustomerId = customerId; Reason = reason; State = state; ReportEvidence = report;
        Originals = Array.AsReadOnly(originals.ToArray()); Attempts = Array.AsReadOnly(attempts.ToArray());
        Shipments = Array.AsReadOnly(shipments.ToArray()); Returns = Array.AsReadOnly(returns.ToArray());
        Audit = Array.AsReadOnly(audit.ToArray()); Revision = Audit.Count;
        Waivers = Array.AsReadOnly((waivers ?? []).ToArray());
        RecoveryFacts = Array.AsReadOnly((recoveryFacts ?? []).ToArray());
        ReturnDecision = returnDecision; BlockProductionUntilReturn = blockProduction; BlockShipmentUntilReturn = blockShipment;
    }

    public static ReplacementCase Create(int customerId, ReplacementReason reason, IReadOnlyList<ReplacementOriginal> originals,
        ReplacementEvidence evidence, int employeeId, DateTimeOffset now)
    {
        Require(customerId > 0 && Enum.IsDefined(reason), "Invalid customer or replacement reason.");
        Require(originals.Count is > 0 and <= 100, "Select between one and 100 affected orders.");
        Require(originals.Select(x => x.OrderId).Distinct().Count() == originals.Count, "Duplicate original order.");
        foreach (var original in originals)
        {
            Require(original.OrderId > 0 && original.CustomerId == customerId, "Original order customer does not match.");
            Require(original.Quantity > 0 && original.Manufactured > 0 && original.AffectedQuantity > 0
                && original.AffectedQuantity <= original.Manufactured && original.AffectedQuantity <= original.Quantity,
                "Affected quantity exceeds original fulfillment.");
            Require(original.QuotationId is null or > 0 && original.InvoiceId is null or > 0, "Invalid original financial reference.");
        }
        ValidateEvidence(evidence); ValidateActor(employeeId, now);
        return new(customerId, reason, ReplacementState.Reported, evidence, originals, [], [], [],
            [new(1, "Reported", employeeId, now, reason.ToString())], null, false, false);
    }

    public ReplacementCase Approve(ReturnDecision returnDecision, bool blockProduction, bool blockShipment,
        string reason, int employeeId, DateTimeOffset now)
    {
        Require(State == ReplacementState.Reported && Enum.IsDefined(returnDecision), "Case is not awaiting approval.");
        Require(returnDecision != Replacement.ReturnDecision.Waived || (!blockProduction && !blockShipment),
            "A waived return cannot gate production or shipping.");
        return Next("Approved", reason, employeeId, now, state: ReplacementState.Approved,
            returnDecision: returnDecision, blockProduction: blockProduction, blockShipment: blockShipment);
    }

    public ReplacementCase Reject(string reason, int employeeId, DateTimeOffset now)
    {
        Require(State == ReplacementState.Reported, "Case is not awaiting approval.");
        return Next("Rejected", reason, employeeId, now, state: ReplacementState.Rejected);
    }

    public ReplacementCase RecordReturn(int orderId, int quantity, ReplacementEvidence evidence, string disposition,
        int employeeId, DateTimeOffset now)
    {
        Approved(); var original = Original(orderId); ValidateEvidence(evidence);
        Require(ReturnDecision == Replacement.ReturnDecision.Required, "Case return is waived.");
        Require(quantity > 0 && quantity <= original.AffectedQuantity - Returns.Where(x => x.OrderId == orderId).Sum(x => x.Quantity),
            "Return quantity exceeds affected quantity.");
        return Next("ReturnReceived", disposition, employeeId, now, returns: Returns.Append(new(orderId, quantity, evidence, disposition)));
    }

    public ReplacementCase StartAttempt(int orderId, int quantity, int employeeId, DateTimeOffset now)
    {
        Approved(); var original = Original(orderId);
        Require(!BlockProductionUntilReturn || ReturnComplete(orderId), "Required return is incomplete.");
        var pending = Attempts.Where(x => x.OrderId == orderId && x.ProducedQuantity is null).Sum(x => x.Quantity);
        var available = Attempts.Where(x => x.OrderId == orderId).Sum(x => (long)x.AcceptedQuantity)
            - Shipments.Where(x => x.OrderId == orderId).Sum(x => (long)x.Quantity);
        var committedShipments = Shipments.Where(x => x.OrderId == orderId &&
            (x.Outcome != ShipmentOutcome.Failed || !x.RetryAuthorized)).Sum(x => (long)x.Quantity);
        var waived = Waivers.Where(x => x.OrderId == orderId).Sum(x => x.Quantity);
        var open = checked(original.AffectedQuantity - waived - pending - available - committedShipments);
        Require(quantity > 0 && quantity <= open, "Attempt quantity exceeds authorized open demand.");
        var id = checked(Attempts.Count + 1);
        return Next("ProductionStarted", $"Order {orderId}, quantity {quantity}", employeeId, now,
            attempts: Attempts.Append(new(id, orderId, quantity, now)));
    }

    public ReplacementCase CompleteQa(int attemptId, int produced, int accepted, int rejected, DateOnly finishedDate,
        ReplacementEvidence evidence, int employeeId, DateTimeOffset now)
    {
        Approved(); var attempt = Attempt(attemptId); ValidateEvidence(evidence);
        Require(attempt.ProducedQuantity is null, "Production attempt is already finished.");
        Require(produced >= 0 && produced <= attempt.Quantity && accepted >= 0 && rejected >= 0
            && (long)accepted + rejected == produced, "QA counts do not reconcile with production.");
        Require(finishedDate >= DateOnly.FromDateTime(attempt.StartedAt.UtcDateTime)
            && finishedDate <= DateOnly.FromDateTime(now.UtcDateTime), "Invalid production finished date.");
        var completed = attempt with
        {
            ProducedQuantity = produced,
            AcceptedQuantity = accepted,
            RejectedQuantity = rejected,
            FinishedDate = finishedDate,
            QaEvidence = evidence
        };
        return Next("QaCompleted", $"Attempt {attemptId}, produced {produced}, accepted {accepted}, rejected {rejected}",
            employeeId, now, attempts: Attempts.Select(x => x.Id == attemptId ? completed : x));
    }

    public ReplacementCase Ship(int attemptId, int quantity, string carrier, string trackingNumber,
        string destinationSnapshot, DateOnly shippedDate, int employeeId, DateTimeOffset now)
    {
        Approved(); var attempt = Attempt(attemptId);
        Require(attempt.ProducedQuantity is not null, "QA must finish before dispatch.");
        Require(!BlockShipmentUntilReturn || ReturnComplete(attempt.OrderId), "Required return is incomplete.");
        var available = attempt.AcceptedQuantity - Shipments.Where(x => x.AttemptId == attemptId).Sum(x => x.Quantity);
        Require(quantity > 0 && quantity <= available, "Shipment quantity exceeds unshipped QA-accepted production.");
        Text(carrier, 100); Text(trackingNumber, 250); Text(destinationSnapshot, 2000);
        Require(shippedDate >= attempt.FinishedDate && shippedDate <= DateOnly.FromDateTime(now.UtcDateTime), "Invalid actual shipment date.");
        return Next("Shipped", $"Attempt {attemptId}, quantity {quantity}", employeeId, now,
            shipments: Shipments.Append(new(checked(Shipments.Count + 1), attemptId, attempt.OrderId,
                quantity, carrier, trackingNumber, destinationSnapshot, shippedDate)));
    }

    public ReplacementCase ConfirmDelivery(int shipmentId, bool delivered, ReplacementEvidence evidence,
        string reason, int employeeId, DateTimeOffset now)
    {
        Approved(); var shipment = Shipment(shipmentId); ValidateEvidence(evidence);
        Require(shipment.Outcome == ShipmentOutcome.InTransit, "Shipment outcome is already recorded.");
        var updated = shipment with { Outcome = delivered ? ShipmentOutcome.Delivered : ShipmentOutcome.Failed, DeliveryEvidence = evidence };
        return Next(delivered ? "Delivered" : "DeliveryFailed", reason, employeeId, now,
            shipments: Shipments.Select(x => x.Id == shipmentId ? updated : x));
    }

    public ReplacementCase AuthorizeRetry(int failedShipmentId, string reason, int employeeId, DateTimeOffset now)
    {
        Approved(); var shipment = Shipment(failedShipmentId);
        Require(shipment.Outcome == ShipmentOutcome.Failed && !shipment.RetryAuthorized, "Failed shipment is not awaiting repeat approval.");
        return Next("RepeatRemedyApproved", reason, employeeId, now,
            shipments: Shipments.Select(x => x.Id == failedShipmentId ? x with { RetryAuthorized = true } : x));
    }

    public int DeliveredQuantity(int orderId) => Shipments.Where(x => x.OrderId == orderId && x.Outcome == ShipmentOutcome.Delivered).Sum(x => x.Quantity);

    public ReplacementCase WaiveRequiredReturn(string reason, int employeeId, DateTimeOffset now)
    {
        Approved();
        Require(ReturnDecision == Replacement.ReturnDecision.Required, "Return is already waived.");
        return Next("ReturnWaived", reason, employeeId, now, returnDecision: Replacement.ReturnDecision.Waived,
            blockProduction: false, blockShipment: false);
    }

    public ReplacementCase WaiveRemaining(int orderId, int quantity, string reason, int employeeId, DateTimeOffset now)
    {
        Approved(); var original = Original(orderId);
        var pending = Attempts.Where(x => x.OrderId == orderId && x.ProducedQuantity is null).Sum(x => (long)x.Quantity);
        var available = Attempts.Where(x => x.OrderId == orderId).Sum(x => (long)x.AcceptedQuantity)
            - Shipments.Where(x => x.OrderId == orderId).Sum(x => (long)x.Quantity);
        var committed = Shipments.Where(x => x.OrderId == orderId &&
            (x.Outcome != ShipmentOutcome.Failed || !x.RetryAuthorized)).Sum(x => (long)x.Quantity);
        var waived = Waivers.Where(x => x.OrderId == orderId).Sum(x => x.Quantity);
        Require(quantity > 0 && quantity <= original.AffectedQuantity - waived - pending - available - committed,
            "Waiver exceeds uncommitted remedy demand.");
        return Next("RemedyQuantityWaived", reason, employeeId, now, waivers: Waivers.Append(new(orderId, quantity, reason)));
    }

    public ReplacementCase Close(int employeeId, DateTimeOffset now)
    {
        Approved();
        Require(Attempts.All(x => x.ProducedQuantity is not null) && Shipments.All(x => x.Outcome != ShipmentOutcome.InTransit),
            "Production or delivery remains open.");
        Require(ReturnDecision != Replacement.ReturnDecision.Required || Originals.All(x => ReturnComplete(x.OrderId)),
            "Required return remains outstanding.");
        Require(Originals.All(x => (long)DeliveredQuantity(x.OrderId) + Waivers.Where(w => w.OrderId == x.OrderId).Sum(w => w.Quantity) == x.AffectedQuantity), "Approved remedy remains undelivered or unwaived.");
        return Next("Closed", "Remedy delivered", employeeId, now, state: ReplacementState.Closed);
    }

    public ReplacementCase RecordRecoveryFact(int orderId, RecoveryFactKind kind, decimal amount, string currency,
        DateOnly observedDate, string description, ReplacementEvidence evidence, string? claimReference, int? correctsFactId,
        int employeeId, DateTimeOffset now)
    {
        Require(State is ReplacementState.Approved or ReplacementState.Closed, "Recovery facts require an approved case.");
        var original = Original(orderId); ValidateEvidence(evidence); Text(description, 2000);
        Require(Enum.IsDefined(kind) && amount >= 0 && amount <= 9999999999999999.99m && decimal.Round(amount, 2) == amount, "Invalid observed amount or kind.");
        Require(currency is { Length: 3 } && currency.All(c => c is >= 'A' and <= 'Z'), "Require an explicit uppercase three-letter currency.");
        Require(observedDate != default && observedDate <= DateOnly.FromDateTime(now.UtcDateTime), "Invalid observation date.");
        if (kind == RecoveryFactKind.Cost) Require(claimReference is null, "A cost is separate from carrier claim observations.");
        else Text(claimReference!, 250);
        if (correctsFactId is { } correctedId)
        {
            var prior = RecoveryFacts.SingleOrDefault(x => x.Id == correctedId);
            Require(prior is not null && prior.OrderId == orderId && prior.Kind == kind && prior.Currency == currency && prior.ClaimReference == claimReference, "Correction must preserve the observed fact identity.");
            Require(!RecoveryFacts.Any(x => x.CorrectsFactId == correctedId), "Correct the latest fact; correction forks are forbidden.");
        }
        var fact = new ReplacementRecoveryFact(checked(RecoveryFacts.Count + 1), orderId, kind, amount, currency, observedDate, description, evidence, claimReference, correctsFactId, original.QuotationId, original.InvoiceId);
        return Next("RecoveryFactRecorded", description, employeeId, now, recoveryFacts: RecoveryFacts.Append(fact));
    }

    private ReplacementCase Next(string action, string reason, int employeeId, DateTimeOffset now,
        ReplacementState? state = null, IEnumerable<ReplacementAttempt>? attempts = null,
        IEnumerable<ReplacementShipment>? shipments = null, IEnumerable<ReplacementReturn>? returns = null,
        ReturnDecision? returnDecision = null, bool? blockProduction = null, bool? blockShipment = null,
        IEnumerable<ReplacementWaiver>? waivers = null, IEnumerable<ReplacementRecoveryFact>? recoveryFacts = null)
    {
        ValidateActor(employeeId, now); Text(reason, 2000);
        Require(now >= Audit[^1].OccurredAt, "Command time precedes case history.");
        return new(CustomerId, Reason, state ?? State, ReportEvidence, Originals, attempts ?? Attempts,
            shipments ?? Shipments, returns ?? Returns,
            Audit.Append(new(checked(Revision + 1), action, employeeId, now, reason)),
            returnDecision ?? ReturnDecision, blockProduction ?? BlockProductionUntilReturn, blockShipment ?? BlockShipmentUntilReturn,
            waivers ?? Waivers, recoveryFacts ?? RecoveryFacts);
    }

    private ReplacementOriginal Original(int id) => Originals.SingleOrDefault(x => x.OrderId == id) ?? throw new ReplacementRuleException("Order is not affected by this case.");
    private ReplacementAttempt Attempt(int id) => Attempts.SingleOrDefault(x => x.Id == id) ?? throw new ReplacementRuleException("Attempt does not belong to this case.");
    private ReplacementShipment Shipment(int id) => Shipments.SingleOrDefault(x => x.Id == id) ?? throw new ReplacementRuleException("Shipment does not belong to this case.");
    private bool ReturnComplete(int id) => Returns.Where(x => x.OrderId == id).Sum(x => x.Quantity) == Original(id).AffectedQuantity;
    private void Approved() => Require(State == ReplacementState.Approved, "Case is not approved and open.");
    private static void ValidateActor(int employeeId, DateTimeOffset now) => Require(employeeId > 0 && now.Offset == TimeSpan.Zero, "Require an employee and UTC command time.");
    private static void ValidateEvidence(ReplacementEvidence evidence) => Require(evidence.DocumentId != Guid.Empty && evidence.VersionId != Guid.Empty, "Require stable document/version evidence.");
    private static void Text(string value, int max) => Require(!string.IsNullOrWhiteSpace(value) && value.Length <= max, "Require bounded nonempty text.");
    private static void Require(bool condition, string reason) { if (!condition) throw new ReplacementRuleException(reason); }
}
