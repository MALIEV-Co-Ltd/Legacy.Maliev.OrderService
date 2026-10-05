using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderMasterQuerySourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("/orders", "%", "1001,1003,1005,1006")]
    [InlineData("/orders/pending", "%", "1001,1003")]
    [InlineData("/orders/customers/7", "%", "1001,1005,1006")]
    [InlineData("/orders", "_", "1002")]
    [InlineData("/orders/pending", "_", "1002")]
    [InlineData("/orders/customers/7", "_", "1002")]
    public async Task ListSearch_SourceContainsTreatsPercentAndUnderscoreAsLiteral(string path, string search, string ids)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, path);
        using var response = await client.GetAsync($"{path}?search={Uri.EscapeDataString(search)}&index=1&size=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        var expected = ids.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(expected, page.Items.Select(item => item.Id));
        Assert.Equal(expected.Length, page.TotalRecords);
        Assert.Equal(1, page.TotalPages);
        await AssertUnchangedAsync(app);
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task ListSearch_SourceRetainsSpacesAroundNonemptyLiteralText(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, path);
        using var response = await client.GetAsync($"{path}?search=%20part%20&index=1&size=10");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUnchangedAsync(app);
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task ListPage_SourceReturnsNotFoundBeyondLastSelectedRow(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, path);
        using var response = await client.GetAsync($"{path}?index=100&size=1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertUnchangedAsync(app);
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string path)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            fixture.Token(path.Contains("/customers/", StringComparison.Ordinal) ? OrderPermissions.CustomerRead : OrderPermissions.Read));
        return client;
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var process = new Process { Name = "Source query process", Category = new Category { Name = "Source query category" } };
        var promised = new DateTime(2026, 10, 10);
        database.Orders.AddRange(
            new Order { Id = 1001, CustomerId = 7, Name = "Fixture%part", TrackingNumber = "track-only", Comment = "comment-only", PromisedDate = promised, Process = process, Quantity = 1 },
            new Order { Id = 1002, CustomerId = 7, Name = "Normalpart", Description = "detail_assembly", PromisedDate = promised, Process = process, Quantity = 1 },
            new Order { Id = 1003, CustomerId = 8, Name = "Other%part", PromisedDate = promised, Process = process, Quantity = 1 },
            new Order { Id = 1004, CustomerId = 7, Name = "Normal", Process = process, Quantity = 1 },
            new Order { Id = 1005, CustomerId = 7, Name = "Finished%part", PromisedDate = promised, FinishedDate = promised.AddDays(-1), Process = process, Quantity = 1 },
            new Order { Id = 1006, CustomerId = 7, Name = null, Description = null, TrackingNumber = "part203", Comment = "tracking%literal", PromisedDate = promised, Process = process, Quantity = 1 });
        await database.SaveChangesAsync();
    }

    private static async Task AssertUnchangedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(6, await database.Orders.CountAsync());
        Assert.Empty(await database.DeletionIntents.ToArrayAsync());
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>().History.ToArrayAsync());
    }
}
