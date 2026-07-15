using System.Reflection;
using Legacy.Maliev.OrderService.Api.Controllers;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderControllerContractTests
{
    public static TheoryData<Type, string> Controllers => new() { { typeof(OrdersController), "[controller]" }, { typeof(CategoriesController), "orders/[controller]" }, { typeof(FileFormatsController), "orders/[controller]" }, { typeof(FilesController), "orders/[controller]" }, { typeof(ProcessesController), "orders/[controller]" }, { typeof(OrderStatusesController), "[controller]" }, { typeof(AvailableStatusesController), "orderstatuses/[controller]" }, { typeof(HistoriesController), "orderstatuses/[controller]" } };
    [Theory, MemberData(nameof(Controllers))] public void Controllers_PreserveRoutesAndRequireAuthentication(Type t, string route) { Assert.Equal(route, t.GetCustomAttribute<RouteAttribute>()?.Template); Assert.NotNull(t.GetCustomAttribute<AuthorizeAttribute>()); }
    [Fact] public void Controllers_PreserveFiftyEightActionsAndTemplates() { var m = Controllers.SelectMany(x => ((Type)x[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray(); Assert.Equal(58, m.Length); Assert.Equal(58, m.SelectMany(x => x.GetCustomAttributes<HttpMethodAttribute>()).Count()); Assert.All(m, x => Assert.Single(x.GetCustomAttributes<RequirePermissionAttribute>())); }
    [Fact]
    public void SignedPermissionClaims_AreAuthoritativeWithoutForcedLiveIamChecks()
    {
        var methods = Controllers.SelectMany(value =>
            ((Type)value[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        Assert.All(methods, method =>
        {
            var authorization = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.False(authorization.RequireLiveCheck);
        });
    }
    [Fact]
    public void CustomerOrderBoundary_HasDedicatedLeastPrivilegeRoutes()
    {
        AssertRouteAndPermission("GetCustomerOrdersAsync", "customers/{customerId:int}", "legacy.customer-orders.read");
        AssertRouteAndPermission("GetCustomerOrderAsync", "customers/{customerId:int}/{id:int}", "legacy.customer-orders.read");
        AssertRouteAndPermission("CancelCustomerOrderAsync", "customers/{customerId:int}/{id:int}/cancel", "legacy.customer-orders.cancel");
        Assert.Null(Assert.Single(typeof(OrdersController).GetMethod(nameof(OrdersController.GetPaginatedOrderAsync))!.GetCustomAttributes<HttpGetAttribute>()).Template);
    }
    [Theory][InlineData(nameof(HistoriesController.CreateOrderHistoryAcceptedStatusAsync), "{orderId:int}/accepted")][InlineData(nameof(HistoriesController.CreateOrderHistoryInProgressStatusAsync), "{orderId:int}/InProgress")][InlineData(nameof(HistoriesController.CreateOrderHistoryShippedStatusAsync), "{orderId:int}/shipped")] public void NamedStatusShortcuts_PreserveLegacyTemplates(string name, string route) => Assert.Equal(route, Assert.Single(typeof(HistoriesController).GetMethod(name)!.GetCustomAttributes<HttpPostAttribute>()).Template);

    private static void AssertRouteAndPermission(string methodName, string route, string permission)
    {
        var method = typeof(OrdersController).GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(route, Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>()).Template);
        var authorization = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(permission, authorization.Permission);
        Assert.False(authorization.RequireLiveCheck);
    }
}
