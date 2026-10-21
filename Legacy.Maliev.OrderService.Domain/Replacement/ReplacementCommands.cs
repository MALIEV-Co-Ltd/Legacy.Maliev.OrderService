using System.Text.Json.Serialization;

namespace Legacy.Maliev.OrderService.Domain.Replacement;

[JsonDerivedType(typeof(RecordReplacementRecoveryFact), "RecordRecoveryFact")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[JsonDerivedType(typeof(ApproveReplacement), "Approve")]
[JsonDerivedType(typeof(RejectReplacement), "Reject")]
[JsonDerivedType(typeof(StartReplacementAttempt), "StartAttempt")]
[JsonDerivedType(typeof(CompleteReplacementQa), "CompleteQa")]
[JsonDerivedType(typeof(RecordReplacementReturn), "RecordReturn")]
[JsonDerivedType(typeof(ShipReplacement), "Ship")]
[JsonDerivedType(typeof(ConfirmReplacementDelivery), "ConfirmDelivery")]
[JsonDerivedType(typeof(AuthorizeReplacementRetry), "AuthorizeRetry")]
[JsonDerivedType(typeof(CloseReplacement), "Close")]
[JsonDerivedType(typeof(WaiveReplacementReturn), "WaiveReturn")]
[JsonDerivedType(typeof(WaiveReplacementQuantity), "WaiveQuantity")]
public abstract record ReplacementCommand
{
    public abstract ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now);
}
public sealed record ApproveReplacement(ReturnDecision ReturnDecision, bool BlockProduction, bool BlockShipment, string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.Approve(ReturnDecision, BlockProduction, BlockShipment, Reason, employeeId, now);
}
public sealed record RejectReplacement(string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.Reject(Reason, employeeId, now);
}
public sealed record StartReplacementAttempt(int OrderId, int Quantity) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.StartAttempt(OrderId, Quantity, employeeId, now);
}
public sealed record CompleteReplacementQa(int AttemptId, int Produced, int Accepted, int Rejected, DateOnly FinishedDate, ReplacementEvidence Evidence) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.CompleteQa(AttemptId, Produced, Accepted, Rejected, FinishedDate, Evidence, employeeId, now);
}
public sealed record RecordReplacementReturn(int OrderId, int Quantity, ReplacementEvidence Evidence, string Disposition) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.RecordReturn(OrderId, Quantity, Evidence, Disposition, employeeId, now);
}
public sealed record ShipReplacement(int AttemptId, int Quantity, string Carrier, string TrackingNumber, string DestinationSnapshot, DateOnly ShippedDate) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.Ship(AttemptId, Quantity, Carrier, TrackingNumber, DestinationSnapshot, ShippedDate, employeeId, now);
}
public sealed record ConfirmReplacementDelivery(int ShipmentId, bool Delivered, ReplacementEvidence Evidence, string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.ConfirmDelivery(ShipmentId, Delivered, Evidence, Reason, employeeId, now);
}
public sealed record AuthorizeReplacementRetry(int ShipmentId, string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.AuthorizeRetry(ShipmentId, Reason, employeeId, now);
}
public sealed record CloseReplacement : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.Close(employeeId, now);
}
public sealed record ReplacementCommandFact(ReplacementCommand Command, int EmployeeId, DateTimeOffset OccurredAt);

public sealed record WaiveReplacementReturn(string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.WaiveRequiredReturn(Reason, employeeId, now);
}
public sealed record WaiveReplacementQuantity(int OrderId, int Quantity, string Reason) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.WaiveRemaining(OrderId, Quantity, Reason, employeeId, now);
}

public sealed record RecordReplacementRecoveryFact(int OrderId, RecoveryFactKind FactKind, decimal Amount, string Currency,
    DateOnly ObservedDate, string Description, ReplacementEvidence Evidence, string? ClaimReference, int? CorrectsFactId) : ReplacementCommand
{
    public override ReplacementCase Apply(ReplacementCase value, int employeeId, DateTimeOffset now) => value.RecordRecoveryFact(OrderId, FactKind, Amount, Currency, ObservedDate, Description, Evidence, ClaimReference, CorrectsFactId, employeeId, now);
}
