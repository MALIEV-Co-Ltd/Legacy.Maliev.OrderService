using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.OrderService.Api;

/// <summary>Bounded default-off recovery using durable due receipts, never an in-process ownership lock.</summary>
public sealed class OrderDeletionRecoveryWorker(IServiceScopeFactory scopes, IOptionsMonitor<OrderDeletionWorkerOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = 10;
            try
            {
                var current = options.CurrentValue;
                if (current.IsValid)
                {
                    delay = current.PollIntervalSeconds;
                    if (current.Enabled)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(current.AttemptTimeoutSeconds));
                        await using var scope = scopes.CreateAsyncScope();
                        await scope.ServiceProvider.GetRequiredService<IOrderDeletionRecovery>()
                            .RecoverPendingDeletionsAsync(current.BatchSize, deadline.Token, current.MaxBackoffSeconds);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* Deadline leaves the durable receipt pending. */ }
            catch (Exception) { /* No token, database identifier, or exception details are logged. Retry remains bounded. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
