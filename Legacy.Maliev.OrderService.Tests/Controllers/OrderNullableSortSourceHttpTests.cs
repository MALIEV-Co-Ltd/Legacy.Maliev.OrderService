using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderNullableSortSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("/orders", "OrderCreatedDate_Ascending")]
    [InlineData("/orders", "2")]
    [InlineData("/orders", "OrderModifiedDate_Ascending")]
    [InlineData("/orders", "4")]
    [InlineData("/orders/pending", "OrderCreatedDate_Ascending")]
    [InlineData("/orders/pending", "2")]
    [InlineData("/orders/pending", "OrderModifiedDate_Ascending")]
    [InlineData("/orders/pending", "4")]
    [InlineData("/orders/customers/7", "OrderCreatedDate_Ascending")]
    [InlineData("/orders/customers/7", "2")]
    [InlineData("/orders/customers/7", "OrderModifiedDate_Ascending")]
    [InlineData("/orders/customers/7", "4")]
    public async Task AscendingDates_SourceNullFirstThroughNamedAndNumericHttp(string path, string sort)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = Client(app, path);
        var expected = sort is "2" or "OrderCreatedDate_Ascending"
            ? new[] { 11, 33, 44, 22 } : new[] { 11, 22, 33, 44 };
        await AssertPageAsync(client, path, sort, 1, 10, expected, 4, 1);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("/orders", "2")]
    [InlineData("/orders", "4")]
    [InlineData("/orders/pending", "2")]
    [InlineData("/orders/pending", "4")]
    [InlineData("/orders/customers/7", "2")]
    [InlineData("/orders/customers/7", "4")]
    public async Task AscendingDates_StableIdentifierTiesBeforePaging(string path, string sort)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = Client(app, path);
        var expected = sort == "2" ? new[] { 11, 33, 44, 22 } : new[] { 11, 22, 33, 44 };
        for (var page = 1; page <= 4; page++)
        {
            await AssertPageAsync(client, path, sort, page, 1, [expected[page - 1]], 4, 4);
        }
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task DescendingAndDefault_KeepExistingNullLastAndIdentifierDirection(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = Client(app, path);
        await AssertPageAsync(client, path, "3", 1, 10, [22, 44, 33, 11], 4, 1);
        await AssertPageAsync(client, path, "5", 1, 10, [44, 33, 22, 11], 4, 1);
        await AssertPageAsync(client, path, "", 1, 10, [11, 22, 33, 44], 4, 1);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task SortedRead_AnonymousAndWrongPermissionCannotDiscloseOrMutate(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = app.CreateClient();
        using var anonymous = await client.GetAsync($"{path}?sort=2&search=Selected&size=10");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var denied = await client.GetAsync($"{path}?sort=4&search=Selected&size=10");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.DoesNotContain("Selected", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string path)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            fixture.Token(path.Contains("/customers/", StringComparison.Ordinal) ? OrderPermissions.CustomerRead : OrderPermissions.Read));
        return client;
    }

    private static async Task AssertPageAsync(HttpClient client, string path, string sort, int index, int size,
        int[] expected, int count, int pages)
    {
        var sortQuery = sort.Length == 0 ? string.Empty : $"sort={sort}&";
        using var response = await client.GetAsync($"{path}?{sortQuery}search=Selected&index={index}&size={size}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        Assert.Equal(expected, result.Items.Select(item => item.Id));
        Assert.Equal(count, result.TotalRecords);
        Assert.Equal(pages, result.TotalPages);
        Assert.Equal(index, result.PageIndex);
        Assert.Equal(index < pages, result.HasNextPage);
        Assert.Equal(index > 1, result.HasPreviousPage);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var process = new Process { Name = "Nullable sort process", Category = new Category { Name = "Nullable sort category" } };
        var day = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Unspecified);
        foreach (var id in new[] { 11, 22, 33, 44 })
        {
            database.Orders.Add(new Order
            {
                Id = id, CustomerId = 7, Name = "Selected", Process = process, Quantity = 1,
                PromisedDate = day.AddDays(10), CreatedDate = id == 22 ? day.AddDays(2) : day,
                ModifiedDate = id == 22 ? day : day.AddDays(2),
            });
        }
        database.Orders.AddRange(
            new Order { Id = 55, CustomerId = 8, Name = "Other customer", Process = process, Quantity = 1, PromisedDate = day },
            new Order { Id = 66, CustomerId = 7, Name = "Finished", Process = process, Quantity = 1, PromisedDate = day, FinishedDate = day },
            new Order { Id = 77, CustomerId = 7, Name = "Unpromised", Process = process, Quantity = 1 });
        await database.SaveChangesAsync();
        // Insert defaults populate null timestamps; force real stored NULL after insertion.
        Assert.Equal(1, await database.Orders.Where(x => x.Id == 11).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.CreatedDate, (DateTime?)null).SetProperty(x => x.ModifiedDate, (DateTime?)null)));
        var stored = await database.Orders.AsNoTracking().SingleAsync(x => x.Id == 11);
        Assert.Null(stored.CreatedDate);
        Assert.Null(stored.ModifiedDate);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            Intents = await orders.DeletionIntents.AsNoTracking().OrderBy(x => x.OrderId).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
        });
    }
}
