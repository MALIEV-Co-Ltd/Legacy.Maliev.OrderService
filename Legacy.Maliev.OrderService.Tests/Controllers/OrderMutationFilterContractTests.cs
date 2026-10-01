using System.Reflection;
using Legacy.Maliev.OrderService.Api;
using Legacy.Maliev.OrderService.Api.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderMutationFilterContractTests
{
    [Fact]
    public void UnavailableFilter_IsOnExactlyTheFencedMutationActions_NotReadsOrCatalogWrites()
    {
        var actions = typeof(OrdersController).Assembly.GetTypes().Where(x => typeof(ControllerBase).IsAssignableFrom(x))
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(x => x.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)).ToArray();
        Assert.Equal(58, actions.Length);
        foreach (var action in actions)
        {
            var expected = action.DeclaringType == typeof(HistoriesController) && action.Name is not "GetLatestAsync" and not "GetOrderHistoryAsync"
                || action.DeclaringType == typeof(FilesController) && action.Name is "CreateOrderFileEntryAsync" or "DeleteOrderFileAsync" or "UpdateOrderFileAsync"
                || action.DeclaringType == typeof(OrdersController) && action.Name is "UpdateOrderAsync" or "CancelCustomerOrderAsync";
            Assert.Equal(expected, action.GetCustomAttribute<OrderMutationUnavailableAttribute>() is not null);
        }
        Assert.Equal(22, actions.Count(x => x.GetCustomAttribute<OrderMutationUnavailableAttribute>() is not null));
    }

    [Fact]
    public void LifetimeConflictFilter_IsOnExactlyLifetimeAwareActions_NotCatalogOrCreate()
    {
        var actions = typeof(OrdersController).Assembly.GetTypes().Where(x => typeof(ControllerBase).IsAssignableFrom(x))
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(x => x.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute)).ToArray();
        Assert.Equal(58, actions.Length);
        foreach (var action in actions)
        {
            var expected = action.DeclaringType == typeof(HistoriesController) || action.DeclaringType == typeof(FilesController)
                || action.DeclaringType == typeof(OrdersController) && action.Name is not "CreateOrderAsync" and not "DeleteOrderAsync";
            Assert.Equal(expected, action.GetCustomAttributes().Any(a => a.GetType().Name == "OrderLifetimeConflictAttribute"));
        }
        Assert.Equal(31, actions.Count(x => x.GetCustomAttributes().Any(a => a.GetType().Name == "OrderLifetimeConflictAttribute")));
    }
}
