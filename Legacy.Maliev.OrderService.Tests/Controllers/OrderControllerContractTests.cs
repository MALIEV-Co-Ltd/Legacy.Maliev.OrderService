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
    [Fact] public void Controllers_PreserveFiftyFiveActionsAndFiftySixTemplates() { var m = Controllers.SelectMany(x => ((Type)x[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray(); Assert.Equal(55, m.Length); Assert.Equal(56, m.SelectMany(x => x.GetCustomAttributes<HttpMethodAttribute>()).Count()); Assert.All(m, x => Assert.Single(x.GetCustomAttributes<RequirePermissionAttribute>())); }
    [Fact] public void OrdersList_PreservesBaseAndCustomerTemplates() { var r = typeof(OrdersController).GetMethod(nameof(OrdersController.GetPaginatedOrderAsync))!.GetCustomAttributes<HttpGetAttribute>().Select(x => x.Template).ToArray(); Assert.Equal(new string?[] { null, "customers/{customerId:int}" }, r); }
    [Theory][InlineData(nameof(HistoriesController.CreateOrderHistoryAcceptedStatusAsync), "{orderId:int}/accepted")][InlineData(nameof(HistoriesController.CreateOrderHistoryInProgressStatusAsync), "{orderId:int}/InProgress")][InlineData(nameof(HistoriesController.CreateOrderHistoryShippedStatusAsync), "{orderId:int}/shipped")] public void NamedStatusShortcuts_PreserveLegacyTemplates(string name, string route) => Assert.Equal(route, Assert.Single(typeof(HistoriesController).GetMethod(name)!.GetCustomAttributes<HttpPostAttribute>()).Template);
}
