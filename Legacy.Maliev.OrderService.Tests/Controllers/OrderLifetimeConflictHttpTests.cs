using System.Net;
using System.Net.Http.Headers;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderLifetimeConflictHttpTests(OrderDeletionReadinessFixture fixture) : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("detail")]
    [InlineData("list")]
    [InlineData("pending")]
    [InlineData("customer-list")]
    [InlineData("customer-detail")]
    [InlineData("history")]
    [InlineData("latest")]
    [InlineData("file")]
    [InlineData("files")]
    public async Task NormalProduction_LiveOrderAndIntentIs409WithoutCachedOrProjectedDisclosure(string route)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(orders, statuses);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.Read));
        using var primed = await client.GetAsync($"/orders/{seed.OrderId}");
        Assert.Equal(HttpStatusCode.OK, primed.StatusCode);
        Assert.Contains("Owned lifetime fixture", await primed.Content.ReadAsStringAsync());
        var now = DateTime.UtcNow;
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = seed.OrderId, DeletionId = Guid.NewGuid(), RequestedAtUtc = now, NextAttemptAtUtc = now });
        await orders.SaveChangesAsync();
        var (path, permission) = route switch
        {
            "detail" => ($"/orders/{seed.OrderId}", OrderPermissions.Read),
            "list" => ("/orders", OrderPermissions.Read),
            "pending" => ("/orders/pending", OrderPermissions.Read),
            "customer-list" => ("/orders/customers/42", OrderPermissions.CustomerRead),
            "customer-detail" => ($"/orders/customers/42/{seed.OrderId}", OrderPermissions.CustomerRead),
            "history" => ($"/orderstatuses/histories/{seed.OrderId}", OrderPermissions.StatusRead),
            "latest" => ($"/orderstatuses/histories/{seed.OrderId}/latest", OrderPermissions.StatusRead),
            "file" => ($"/orders/files/{seed.FileId}", OrderPermissions.FilesRead),
            _ => ($"/orders/{seed.OrderId}/files", OrderPermissions.FilesRead),
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Owned lifetime fixture", body);
        Assert.DoesNotContain("owned-object.stl", body);
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.Single(await orders.Orders.AsNoTracking().ToListAsync());
        Assert.Single(await orders.Files.AsNoTracking().ToListAsync());
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        Assert.Null((await orders.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
    }

    [Fact]
    public async Task CustomerListDoesNotDiscloseUnrelatedLifetimeConflict()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(orders, statuses);
        var now = DateTime.UtcNow;
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = seed.OrderId, DeletionId = Guid.NewGuid(), RequestedAtUtc = now, NextAttemptAtUtc = now });
        await orders.SaveChangesAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CustomerRead));
        using var response = await client.GetAsync("/orders/customers/99");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(await orders.Orders.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("/orders?search=unrelated", false)]
    [InlineData("/orders/pending?search=unrelated", false)]
    [InlineData("/orders/customers/42?search=unrelated", true)]
    [InlineData("/orders?search=Owned&index=2&size=1", false)]
    public async Task FilteredListScopesConflictBeforePagination(string path, bool customer)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(orders, statuses);
        var now = DateTime.UtcNow;
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = seed.OrderId, DeletionId = Guid.NewGuid(), RequestedAtUtc = now, NextAttemptAtUtc = now });
        await orders.SaveChangesAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(customer ? OrderPermissions.CustomerRead : OrderPermissions.Read));
        using var response = await client.GetAsync(path);
        // Matching lifetime conflicts cannot disappear merely by requesting a later page.
        Assert.Equal(path.Contains("index=2", StringComparison.Ordinal) ? HttpStatusCode.Conflict : HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Owned lifetime fixture", await response.Content.ReadAsStringAsync());
        Assert.Single(await orders.Orders.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task UnknownAuthorityQueryFailureRemains500_Not409Or503()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await orders.Database.ExecuteSqlRawAsync("DROP TABLE \"OrderDeletionIntent\"");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.Read));
        using var response = await client.GetAsync("/orders/901");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Npgsql", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingLifetimeStillReturnsNormal404()
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.Read));
        using var response = await client.GetAsync("/orders/901");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<(int OrderId, int FileId)> SeedAsync(OrderDbContext orders, OrderStatusDbContext statuses)
    {
        var order = new Order { Name = "Owned lifetime fixture", CustomerId = 42, Quantity = 1, PromisedDate = DateTime.UtcNow.Date, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        var file = new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned-object.stl" };
        orders.Add(file);
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        return (order.Id, file.Id);
    }
}
