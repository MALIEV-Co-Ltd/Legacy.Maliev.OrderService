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

    [Theory]
    [InlineData("/orders", "1001")]
    [InlineData("/orders", "001001")]
    [InlineData("/orders", " 1001 ")]
    [InlineData("/orders/pending", "1001")]
    [InlineData("/orders/pending", "001001")]
    [InlineData("/orders/pending", " 1001 ")]
    [InlineData("/orders/customers/7", "1001")]
    [InlineData("/orders/customers/7", "001001")]
    [InlineData("/orders/customers/7", " 1001 ")]
    public async Task ListSearch_KeepsExistingExclusiveIntegerBranch(string path, string search)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, path);
        using var response = await client.GetAsync($"{path}?search={Uri.EscapeDataString(search)}&size=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        Assert.Equal(1001, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalRecords);
        await AssertUnchangedAsync(app);
    }

    [Theory]
    [InlineData("track-only")]
    [InlineData("comment-only")]
    public async Task PendingSearch_KeepsOnlyNameAndDescriptionWhileRegularIncludesOtherFields(string search)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, "/orders");
        using var regular = await client.GetAsync($"/orders?search={search}&size=10");
        Assert.Equal(HttpStatusCode.OK, regular.StatusCode);
        Assert.Equal(1001, Assert.Single((await regular.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!.Items).Id);
        using var pending = await client.GetAsync($"/orders/pending?search={search}&size=10");
        Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode);
        await AssertUnchangedAsync(app);
    }

    [Fact]
    public async Task LiteralThaiText_RemainsExactThroughHttpAndPostgreSql()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var client = Client(app, "/orders");
        const string search = "\u0e0a\u0e34\u0e49\u0e19\u0e07\u0e32\u0e19";
        using var response = await client.GetAsync($"/orders?search={Uri.EscapeDataString(search)}&size=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = (await response.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        Assert.Equal(1001, Assert.Single(page.Items).Id);
        Assert.EndsWith(search, page.Items[0].Name, StringComparison.Ordinal);
        await AssertUnchangedAsync(app);
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task MatchingDeletedLifetime_StillConflictsBeforeEmptyPageSelection(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var now = DateTime.UtcNow;
            database.DeletionIntents.Add(new OrderDeletionIntent { OrderId = 1001, DeletionId = Guid.NewGuid(), RequestedAtUtc = now, NextAttemptAtUtc = now });
            await database.SaveChangesAsync();
        }
        using var client = Client(app, path);
        using var response = await client.GetAsync($"{path}?search=%25&index=100&size=1");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Fixture%part", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await using var readScope = app.Services.CreateAsyncScope();
        var orders = readScope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(6, await orders.Orders.CountAsync());
        Assert.Single(await orders.DeletionIntents.ToArrayAsync());
        Assert.Empty(await readScope.ServiceProvider.GetRequiredService<OrderStatusDbContext>().History.ToArrayAsync());
    }


    [Theory]
    [InlineData("/orders", false)]
    [InlineData("/orders", true)]
    [InlineData("/orders/pending", false)]
    [InlineData("/orders/pending", true)]
    [InlineData("/orders/customers/7", false)]
    [InlineData("/orders/customers/7", true)]
    public async Task ListOmittedSize_ReturnsAllMatchesAndPascalPager(string path, bool filtered)
    {
        await using var app = await fixture.AppAsync(null);
        var count = filtered ? 251 : 51;
        await SeedOmittedSizeAsync(app, path, count, filtered);
        var before = await ReadListStateAsync(app);
        using var client = Client(app, path);
        var query = filtered ? "?search=Omitted" : "?";
        foreach (var suffix in new[] { "", "size=" })
        {
            using var response = await client.GetAsync($"{path}{query}{(filtered ? "&" : "")}{suffix}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal(new[] { "hasNextPage", "hasPreviousPage", "items", "pageIndex", "totalPages", "totalRecords" },
                root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(Enumerable.Range(1, count), root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
            Assert.Equal(1, root.GetProperty("pageIndex").GetInt32());
            Assert.Equal(count, root.GetProperty("totalRecords").GetInt32());
            Assert.Equal(1, root.GetProperty("totalPages").GetInt32());
            Assert.False(root.GetProperty("hasNextPage").GetBoolean());
            Assert.False(root.GetProperty("hasPreviousPage").GetBoolean());
            var page = (await response.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
            Assert.All(page.Items, item =>
            {
                Assert.Equal(7, item.CustomerId);
                Assert.Equal("Omitted match", item.Name);
                Assert.Null(item.FinishedDate);
            });
        }
        using var beyond = await client.GetAsync($"{path}{query}{(filtered ? "&" : "")}index=2");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
        Assert.Equal(before, await ReadListStateAsync(app));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task ListExplicitSizeAndAdmissionGuards_RemainUnchanged(string path)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, path);
        var emptyState = await ReadListStateAsync(app);
        using var empty = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Equal(emptyState, await ReadListStateAsync(app));
        await SeedOmittedSizeAsync(app, path, 251, false);
        var before = await ReadListStateAsync(app);
        using var second = await client.GetAsync($"{path}?size=10&index=2");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var page = (await second.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        Assert.Equal(Enumerable.Range(11, 10), page.Items.Select(item => item.Id));
        Assert.Equal((2, 26, 251, true, true),
            (page.PageIndex, page.TotalPages, page.TotalRecords, page.HasNextPage, page.HasPreviousPage));
        using var bounded = await client.GetAsync($"{path}?size=251&index=0");
        Assert.Equal(HttpStatusCode.OK, bounded.StatusCode);
        var capped = (await bounded.Content.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
        Assert.Equal(Enumerable.Range(1, 250), capped.Items.Select(item => item.Id));
        Assert.Equal((1, 2, 251, true, false),
            (capped.PageIndex, capped.TotalPages, capped.TotalRecords, capped.HasNextPage, capped.HasPreviousPage));
        using var missing = await client.GetAsync($"{path}?search=absent-list-row");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var beyond = await client.GetAsync($"{path}?size=10&index=1000");
        Assert.Equal(HttpStatusCode.NotFound, beyond.StatusCode);
        if (path.Contains("/customers/", StringComparison.Ordinal))
        {
            using var missingCustomer = await client.GetAsync("/orders/customers/999");
            Assert.Equal(HttpStatusCode.NotFound, missingCustomer.StatusCode);
        }
        using var anonymous = app.CreateClient();
        using var unauthenticated = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var denied = app.CreateClient();
        denied.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var forbidden = await denied.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(before, await ReadListStateAsync(app));
    }

    [Theory]
    [InlineData("/orders")]
    [InlineData("/orders/pending")]
    [InlineData("/orders/customers/7")]
    public async Task ListOmittedSize_DeletionConflictRemainsReadOnly(string path)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedOmittedSizeAsync(app, path, 51, false);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var now = DateTime.UtcNow;
            database.DeletionIntents.Add(new OrderDeletionIntent
            {
                OrderId = 1,
                DeletionId = Guid.NewGuid(),
                RequestedAtUtc = now,
                NextAttemptAtUtc = now
            });
            await database.SaveChangesAsync();
        }
        var before = await ReadListStateAsync(app);
        using var client = Client(app, path);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("Omitted match", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await ReadListStateAsync(app));
    }

    private static async Task SeedOmittedSizeAsync(WebApplicationFactory<Program> app, string path, int count, bool filtered)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var process = new Process { Name = "Omitted size process", Category = new Category { Name = "Omitted size category" } };
        var promised = new DateTime(2026, 10, 10);
        for (var id = 1; id <= count; id++)
            database.Orders.Add(new Order { Id = id, CustomerId = 7, Name = "Omitted match", PromisedDate = promised, Process = process, Quantity = 1 });
        if (filtered)
        {
            database.Orders.Add(new Order { Id = 1001, CustomerId = 7, Name = "Excluded one", PromisedDate = promised, Process = process, Quantity = 1 });
            database.Orders.Add(new Order { Id = 1002, CustomerId = 7, Name = "Excluded two", PromisedDate = promised, Process = process, Quantity = 1 });
        }
        if (path == "/orders/pending")
        {
            database.Orders.Add(new Order { Id = 1003, CustomerId = 7, Name = "Omitted match", Process = process, Quantity = 1 });
            database.Orders.Add(new Order { Id = 1004, CustomerId = 7, Name = "Omitted match", PromisedDate = promised, FinishedDate = promised, Process = process, Quantity = 1 });
        }
        if (path.Contains("/customers/", StringComparison.Ordinal))
            database.Orders.Add(new Order { Id = 1005, CustomerId = 8, Name = "Omitted match", PromisedDate = promised, Process = process, Quantity = 1 });
        await database.SaveChangesAsync();
    }

    private static async Task<string> ReadListStateAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        // Fresh no-tracking queries retain all mapped scalars, computed values and
        // concurrency dates. Unloaded navigation members cannot cause EF fix-up.
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            FileFormats = await orders.FileFormats.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(item => item.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync()
        });
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
            new Order { Id = 1001, CustomerId = 7, Name = "Fixture%part\u0e0a\u0e34\u0e49\u0e19\u0e07\u0e32\u0e19", TrackingNumber = "track-only", Comment = "comment-only", PromisedDate = promised, Process = process, Quantity = 1 },
            new Order { Id = 1002, CustomerId = 7, Name = "Normalpart1001", Description = "detail_assembly", PromisedDate = promised, Process = process, Quantity = 1 },
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
