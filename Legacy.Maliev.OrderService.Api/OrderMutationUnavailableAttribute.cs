using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Legacy.Maliev.OrderService.Api;

/// <summary>Only the explicitly fenced mutation actions map a verified unavailable boundary to503.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OrderMutationUnavailableAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not OrderMutationUnavailableException || context.HttpContext.RequestAborted.IsCancellationRequested) return;
        context.Result = new ObjectResult("Order mutation is temporarily unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
        context.ExceptionHandled = true;
    }
}
