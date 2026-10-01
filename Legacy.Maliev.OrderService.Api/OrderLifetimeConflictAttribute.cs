using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Legacy.Maliev.OrderService.Api;

/// <summary>Only lifetime-aware actions translate the exact durable-lifetime conflict type.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OrderLifetimeConflictAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        if (context.Exception is not OrderDeletionConflictException || context.HttpContext.RequestAborted.IsCancellationRequested) return;
        context.Result = new ObjectResult("Order lifetime conflicts with its deletion receipt.") { StatusCode = StatusCodes.Status409Conflict };
        context.ExceptionHandled = true;
    }
}
