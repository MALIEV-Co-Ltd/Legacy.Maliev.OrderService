using Legacy.Maliev.OrderService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.OrderService.Api;

/// <summary>Local fail-closed admission after unchanged JWT/permission authorization.</summary>
public sealed class OrderDeletionAdmissionFilter(OrderDbContext orders, IOptionsMonitor<OrderDeletionRecoveryOptions> options) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var cancellation = context.HttpContext.RequestAborted;
        cancellation.ThrowIfCancellationRequested();
        bool enabled;
        try
        {
            enabled = options.CurrentValue.Enabled;
        }
        catch (OptionsValidationException)
        {
            Unavailable(context);
            return;
        }
        catch (InvalidOperationException)
        {
            Unavailable(context);
            return;
        }
        if (!enabled || !await OrderDeletionSchemaReadiness.CheckAsync(orders, cancellation))
        {
            Unavailable(context);
            return;
        }
        await next();
    }

    private static void Unavailable(ActionExecutingContext context) => context.Result = new ObjectResult("Order deletion is temporarily unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
}
