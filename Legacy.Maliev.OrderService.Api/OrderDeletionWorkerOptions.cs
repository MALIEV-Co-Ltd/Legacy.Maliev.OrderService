namespace Legacy.Maliev.OrderService.Api;

/// <summary>Code-only recovery admission. No production activation is provided by this configuration.</summary>
public sealed class OrderDeletionWorkerOptions
{
    public bool Enabled { get; set; }
    public int BatchSize { get; set; } = 20;
    public int PollIntervalSeconds { get; set; } = 10;
    public int AttemptTimeoutSeconds { get; set; } = 10;
    public int MaxBackoffSeconds { get; set; } = 300;
    public bool IsValid => BatchSize is >= 1 and <= 100 && PollIntervalSeconds is >= 1 and <= 60
        && AttemptTimeoutSeconds is >= 1 and <= 60 && MaxBackoffSeconds is >= 1 and <= 300;
}
