namespace Legacy.Maliev.OrderService.Domain;

/// <summary>Durable cleanup authority for one deleted Order lifetime; contains no personal data.</summary>
public sealed class OrderDeletionIntent
{
    public int OrderId { get; set; }
    public Guid DeletionId { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? StatusCleanupCompletedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
}
