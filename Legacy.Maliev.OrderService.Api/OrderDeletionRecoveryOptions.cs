namespace Legacy.Maliev.OrderService.Api;

/// <summary>Explicit default-off HTTP deletion admission; this does not activate a worker.</summary>
public sealed class OrderDeletionRecoveryOptions
{
    public bool Enabled { get; set; }
}
