using Legacy.Maliev.OrderService.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Legacy.Maliev.OrderService.Api;

public sealed class OrderDeletionReadinessHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var ready = await OrderDeletionSchemaReadiness.CheckAsync(scope.ServiceProvider.GetRequiredService<OrderDbContext>(), cancellationToken);
        return ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Order deletion schema is not ready.");
    }
}
