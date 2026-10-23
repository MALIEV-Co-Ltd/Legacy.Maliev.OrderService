using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class ProcessReadSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("Additive", false)]
    [InlineData("Additive", true)]
    [InlineData("Electronics", false)]
    [InlineData("Electronics", true)]
    [InlineData("Machining", false)]
    [InlineData("Machining", true)]
    [InlineData("Scanning", false)]
    [InlineData("Scanning", true)]
    public async Task FilteredReadsPreserveOriginalUntrackedCategoryGraph(string category, bool nullDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await SeedAsync(database, category, nullDates);
        var before = await SnapshotAsync(database);
        var originals = await database.Processes.Include(row => row.Category).AsNoTracking()
            .Where(row => row.Category!.Name == category).OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(2, originals.Length);
        Assert.NotSame(originals[0].Category, originals[1].Category);
        foreach (var row in originals)
        {
            Assert.Same(row, Assert.Single(row.Category!.Processes));
            Assert.Empty(row.Orders);
        }
        Assert.Empty(database.ChangeTracker.Entries());
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orders/processes/{category.ToLowerInvariant()}", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(OriginalGraph(originals))), JsonNode.Parse(body)));
        using var wire = JsonDocument.Parse(body);
        foreach (var row in wire.RootElement.EnumerateArray())
        {
            Assert.Empty(row.GetProperty("order").EnumerateArray());
            Assert.Empty(row.GetProperty("category").GetProperty("process").EnumerateArray());
        }
        // The inspected Web reader uses default ReadFromJsonAsync and these three scalars.
        var consumer = await response.Content.ReadFromJsonAsync<WebProcessConsumer[]>(deadline.Token);
        Assert.Equal(originals.Select(row => new WebProcessConsumer(row.Id, row.CategoryId, row.Name)), consumer!);
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Empty(await database.Orders.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfilteredAndDetailReadsKeepNavigationUnloaded(bool nullDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await SeedAsync(database, "Additive", nullDates);
        var originals = await database.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(3, originals.Length);
        Assert.All(originals, row => Assert.Null(row.Category));
        var before = await SnapshotAsync(database);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var list = await client.GetAsync("/orders/processes", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(OriginalGraph(originals))),
            JsonNode.Parse(await list.Content.ReadAsStringAsync(deadline.Token))));
        foreach (var original in originals)
        {
            using var response = await client.GetAsync($"/orders/processes/{original.Id}", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(OriginalGraph([original])[0])), JsonNode.Parse(body)));
            using var wire = JsonDocument.Parse(body);
            Assert.False(wire.RootElement.TryGetProperty("category", out _));
            Assert.Empty(wire.RootElement.GetProperty("order").EnumerateArray());
        }
        Assert.Equal(before, await SnapshotAsync(database));
    }

    [Theory]
    [InlineData("additive")]
    [InlineData("electronics")]
    [InlineData("machining")]
    [InlineData("scanning")]
    [InlineData("")]
    [InlineData("2147483647")]
    public async Task EveryReadRetainsMissingAndPermissionResponses(string suffix)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var path = "/orders/processes/" + suffix;
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized),
            (OrderPermissions.StatusRead, HttpStatusCode.Forbidden),
            (OrderPermissions.CatalogRead, HttpStatusCode.NotFound),
        })
        {
            client.DefaultRequestHeaders.Authorization = permission is null ? null
                : new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(expected, response.StatusCode);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Empty(await database.Processes.ToArrayAsync());
        Assert.Empty(await database.Categories.ToArrayAsync());
    }

    [Fact]
    public async Task OriginalUntrackedAndIdentityResolvingQueriesHaveDifferentInverseGraphs()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await SeedAsync(database, "Additive", false);
        var query = database.Processes.Include(row => row.Category).Where(row => row.Category!.Name == "Additive");
        var untracked = await query.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        var resolving = await query.AsNoTrackingWithIdentityResolution().OrderBy(row => row.Id).ToArrayAsync();
        Assert.NotSame(untracked[0].Category, untracked[1].Category);
        Assert.Same(resolving[0].Category, resolving[1].Category);
        Assert.All(untracked, row => Assert.Same(row, Assert.Single(row.Category!.Processes)));
        Assert.Equal(2, resolving[0].Category!.Processes.Count);
        using var originalWire = JsonDocument.Parse(SerializeOriginal(OriginalGraph(untracked)));
        using var changedWire = JsonDocument.Parse(SerializeOriginal(OriginalGraph(resolving)));
        Assert.Empty(originalWire.RootElement[0].GetProperty("category").GetProperty("process").EnumerateArray());
        Assert.Equal(resolving[1].Id, Assert.Single(changedWire.RootElement[0].GetProperty("category").GetProperty("process").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.False(JsonNode.DeepEquals(JsonNode.Parse(originalWire.RootElement.GetRawText()), JsonNode.Parse(changedWire.RootElement.GetRawText())));
        Assert.Empty(database.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ServedReadSchemasDoNotChangeInternalOrWriteShapes()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var output = schemas.GetProperty("ProcessReadResponse");
        Assert.Equal(new[] { "category", "categoryId", "createdDate", "id", "modifiedDate", "name", "order" },
            output.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "createdDate", "id", "modifiedDate", "name", "process" },
            schemas.GetProperty("ProcessCategoryReadResponse").GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.True(schemas.GetProperty("ProcessResponse").GetProperty("properties").TryGetProperty("CategoryId", out _));
        Assert.True(schemas.GetProperty("UpsertProcessRequest").GetProperty("properties").TryGetProperty("Name", out _));
        using var internalWire = JsonDocument.Parse(JsonSerializer.Serialize(new ProcessResponse(1, 2, "Internal", null, null)));
        Assert.True(internalWire.RootElement.TryGetProperty("CategoryId", out _));
    }

    private static async Task SeedAsync(OrderDbContext database, string categoryName, bool nullDates)
    {
        var category = new Category { Name = categoryName, CreatedDate = new DateTime(2020, 1, 1), ModifiedDate = new DateTime(2021, 1, 1) };
        var other = new Category { Name = "Other" };
        database.AddRange(category, other);
        await database.SaveChangesAsync();
        database.Processes.AddRange(
            new Process { CategoryId = category.Id, Name = "  ไทย / process A  ", CreatedDate = new DateTime(2022, 1, 1), ModifiedDate = new DateTime(2023, 1, 1) },
            new Process { CategoryId = category.Id, Name = "Process B", CreatedDate = new DateTime(2022, 2, 1), ModifiedDate = new DateTime(2023, 2, 1) },
            new Process { CategoryId = other.Id, Name = "Unrelated" });
        await database.SaveChangesAsync();
        if (nullDates)
        {
            await database.Processes.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null));
            await database.Categories.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null));
        }
        database.ChangeTracker.Clear();
    }

    private static async Task<string> SnapshotAsync(OrderDbContext database) => JsonSerializer.Serialize(new
    {
        Processes = await database.Processes.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
        Categories = await database.Categories.AsNoTracking().OrderBy(row => row.Id).Select(row => new { row.Id, row.Name, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
    });

    private static OriginalProcess[] OriginalGraph(Process[] rows)
    {
        var processes = new Dictionary<Process, OriginalProcess>(ReferenceEqualityComparer.Instance);
        var categories = new Dictionary<Category, OriginalCategory>(ReferenceEqualityComparer.Instance);
        OriginalProcess Map(Process row)
        {
            if (processes.TryGetValue(row, out var existing)) return existing;
            var result = new OriginalProcess { Id = row.Id, CategoryId = row.CategoryId, Name = row.Name, CreatedDate = row.CreatedDate, ModifiedDate = row.ModifiedDate };
            processes.Add(row, result);
            Assert.Empty(row.Orders);
            if (row.Category is { } category)
            {
                if (!categories.TryGetValue(category, out var mapped))
                {
                    mapped = new OriginalCategory { Id = category.Id, Name = category.Name, CreatedDate = category.CreatedDate, ModifiedDate = category.ModifiedDate };
                    categories.Add(category, mapped);
                    foreach (var sibling in category.Processes) mapped.Process.Add(Map(sibling));
                }
                result.Category = mapped;
            }
            return result;
        }
        return rows.Select(Map).ToArray();
    }

    private static string SerializeOriginal(object graph)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(graph, options.SerializerSettings);
    }

    // Original CLR property/collection shapes, applied to the actual no-tracking EF graph.
    // This is a source contract witness, not execution of the original SQL Server binary.
    private sealed class OriginalProcess
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string Name { get; set; } = "";
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public OriginalCategory? Category { get; set; }
        public ICollection<object> Order { get; set; } = new HashSet<object>();
    }

    private sealed class OriginalCategory
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public ICollection<OriginalProcess> Process { get; set; } = new HashSet<OriginalProcess>();
    }

    private sealed record WebProcessConsumer(int Id, int CategoryId, string Name);
}
