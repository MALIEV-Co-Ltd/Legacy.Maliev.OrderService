using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalCategory = Maliev.OrderService.Data.Database.OrderContext.Category;
using OriginalProcess = Maliev.OrderService.Data.Database.OrderContext.Process;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class CatalogResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public async Task CategoryGetMatchesOriginalFreshFindWithUnloadedChildren(bool nullName, int nullDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var category = await SeedAsync(database);
        var name = nullName ? null : "  ไทย / category  ";
        DateTime? created = (nullDates & 1) != 0 ? null : new DateTime(2021, 2, 3, 4, 5, 6);
        DateTime? modified = (nullDates & 2) != 0 ? null : new DateTime(2022, 3, 4, 5, 6, 7);
        Assert.Equal(1, await database.Categories.Where(row => row.Id == category.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(row => row.Name, name).SetProperty(row => row.CreatedDate, created).SetProperty(row => row.ModifiedDate, modified)));
        database.ChangeTracker.Clear();
        var before = await SnapshotAsync(database);
        // FindAsync in a fresh context does not load the two stored child processes.
        var stored = (await database.Categories.FindAsync(category.Id))!;
        Assert.False(database.Entry(stored).Collection(row => row.Processes).IsLoaded);
        Assert.Empty(stored.Processes);
        Assert.Equal(2, await database.Processes.CountAsync(row => row.CategoryId == stored.Id));
        var original = Original(stored);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        using var response = await reader.GetAsync($"/orders/categories/{stored.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertSourceWire(body, original);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(new CategoryResponse(stored.Id, name, created, modified), await content.ReadFromJsonAsync<CategoryResponse>());
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Empty(await database.Orders.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData("category", false)]
    [InlineData("category", true)]
    [InlineData("process", false)]
    public async Task PostReturnsFreshSourceEntityAndLocationWithoutInputNavigations(string kind, bool nullName)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 23, 1, 2, 3, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var parent = await SeedAsync(database);
        var before = await SnapshotAsync(database);
        var name = nullName ? null : "  ไทย / created  ";
        var input = new JsonObject
        {
            ["Id"] = 99999999,
            ["Name"] = name,
            ["CreatedDate"] = "2000-01-01T00:00:00",
            ["ModifiedDate"] = "2000-01-02T00:00:00",
        };
        if (kind == "category")
            input["Process"] = new JsonArray(new JsonObject { ["Id"] = 77777, ["CategoryId"] = parent.Id, ["Name"] = "ignored", ["Order"] = new JsonArray(new JsonObject()) });
        else
        {
            input["CategoryId"] = parent.Id;
            input["Category"] = new JsonObject { ["Id"] = parent.Id, ["Name"] = "ignored", ["Process"] = new JsonArray() };
            input["Order"] = new JsonArray(new JsonObject());
        }
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var payload = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await writer.PostAsync(kind == "category" ? "/orders/categories" : "/orders/processes", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync();
        // Consume the actual body through the unchanged internal DTO; its own serialization stays PascalCase.
        using var consumerBody = new StringContent(body, Encoding.UTF8, "application/json");
        var id = kind == "category"
            ? (await consumerBody.ReadFromJsonAsync<CategoryResponse>())!.Id
            : (await consumerBody.ReadFromJsonAsync<ProcessResponse>())!.Id;
        Assert.NotEqual(99999999, id);
        var now = DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
        object original;
        object locationOriginal;
        if (kind == "category")
        {
            var stored = await database.Categories.AsNoTracking().SingleAsync(row => row.Id == id);
            Assert.Equal(name, stored.Name);
            Assert.Equal(now, stored.CreatedDate);
            Assert.Equal(now, stored.ModifiedDate);
            locationOriginal = Original(stored);
            original = new OriginalCategory
            {
                Id = stored.Id,
                Name = stored.Name,
                CreatedDate = clock.GetUtcNow().UtcDateTime,
                ModifiedDate = clock.GetUtcNow().UtcDateTime
            };
            Assert.Equal(before, await SnapshotAsync(database, excludedCategory: id));
        }
        else
        {
            var stored = await database.Processes.AsNoTracking().SingleAsync(row => row.Id == id);
            Assert.Equal(name, stored.Name);
            Assert.Equal(parent.Id, stored.CategoryId);
            Assert.Equal(now, stored.CreatedDate);
            Assert.Equal(now, stored.ModifiedDate);
            Assert.Null(stored.Category);
            Assert.Empty(stored.Orders);
            locationOriginal = new OriginalProcess { Id = stored.Id, CategoryId = stored.CategoryId, Name = stored.Name, CreatedDate = stored.CreatedDate, ModifiedDate = stored.ModifiedDate };
            original = new OriginalProcess
            {
                Id = stored.Id,
                CategoryId = stored.CategoryId,
                Name = stored.Name,
                CreatedDate = clock.GetUtcNow().UtcDateTime,
                ModifiedDate = clock.GetUtcNow().UtcDateTime
            };
            Assert.Equal(before, await SnapshotAsync(database, excludedProcess: id));
        }
        AssertSourceWire(body, original);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        using var location = await reader.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        AssertSourceWire(await location.Content.ReadAsStringAsync(), locationOriginal);
        Assert.Empty(await database.Orders.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public void OriginalFreshConstructionAndStoredFindHaveDifferentDateLexemes()
    {
        var utc = new DateTime(2026, 10, 23, 1, 2, 3, DateTimeKind.Utc);
        var stored = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var category = new OriginalCategory { Id = 7, CreatedDate = utc };
        var process = new OriginalProcess { Id = 8, CategoryId = 7, Name = "Control", CreatedDate = stored };
        Assert.IsType<HashSet<OriginalProcess>>(category.Process);
        Assert.Null(process.Category);
        Assert.Empty(process.Order);
        using var fresh = JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(category, settings));
        using var find = JsonDocument.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(process, settings));
        Assert.Equal(new[] { "createdDate", "id", "process" }, fresh.RootElement.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal("2026-10-23T01:02:03Z", fresh.RootElement.GetProperty("createdDate").GetString());
        Assert.Equal("2026-10-23T01:02:03", find.RootElement.GetProperty("createdDate").GetString());
        Assert.Empty(fresh.RootElement.GetProperty("process").EnumerateArray());
        Assert.Empty(find.RootElement.GetProperty("order").EnumerateArray());
    }

    [Theory]
    [InlineData("category")]
    [InlineData("process")]
    public async Task UnauthorizedAndWrongPermissionCreatesDoNotWrite(string kind)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var parent = await SeedAsync(database);
        var before = await SnapshotAsync(database);
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized), (OrderPermissions.CatalogRead, HttpStatusCode.Forbidden),
        })
        {
            using var client = Client(app, permission);
            using var response = kind == "category"
                ? await client.PostAsJsonAsync("/orders/categories", new UpsertCategoryRequest("Rejected"))
                : await client.PostAsJsonAsync("/orders/processes", new UpsertProcessRequest(parent.Id, "Rejected"));
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(database));
        Assert.Empty(await database.Orders.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task CategoryReadRetainsMissingAndPermissionResponses()
    {
        await using var app = await fixture.AppAsync(null);
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized), (OrderPermissions.CatalogWrite, HttpStatusCode.Forbidden),
            (OrderPermissions.CatalogRead, HttpStatusCode.NotFound),
        })
        {
            using var client = Client(app, permission);
            using var response = await client.GetAsync("/orders/categories/2147483647");
            Assert.Equal(expected, response.StatusCode);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Empty(await database.Categories.AsNoTracking().ToArrayAsync());
        Assert.Empty(await database.Processes.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task ServedSchemasDescribeSourceResponsesWhileInternalAndInputShapesStayUnchanged()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var category = schemas.GetProperty("CategoryReadResponse");
        Assert.Equal(new[] { "createdDate", "id", "modifiedDate", "name", "process" },
            category.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var processType = category.GetProperty("properties").GetProperty("process").GetProperty("type");
        Assert.Contains("array", processType.ValueKind == JsonValueKind.Array
            ? processType.EnumerateArray().Select(value => value.GetString()).ToArray() : [processType.GetString()]);
        var required = category.TryGetProperty("required", out var requiredArray)
            ? requiredArray.EnumerateArray().Select(value => value.GetString()).ToArray() : [];
        Assert.Contains("id", required);
        foreach (var optional in new[] { "name", "createdDate", "modifiedDate" }) Assert.DoesNotContain(optional, required);
        AssertResponseSchema(root, "/orders/Categories/{id}", "get", "200", "CategoryReadResponse");
        AssertResponseSchema(root, "/orders/Categories", "post", "201", "CategoryReadResponse");
        AssertResponseSchema(root, "/orders/Processes", "post", "201", "ProcessReadResponse");
        Assert.Equal(new[] { "Name" }, schemas.GetProperty("UpsertCategoryRequest").GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.True(schemas.GetProperty("UpsertProcessRequest").GetProperty("properties").TryGetProperty("CategoryId", out _));
        using var internalCategory = JsonDocument.Parse(JsonSerializer.Serialize(new CategoryResponse(7, "Control", null, null)));
        using var internalProcess = JsonDocument.Parse(JsonSerializer.Serialize(new ProcessResponse(8, 7, "Control", null, null)));
        Assert.True(internalCategory.RootElement.TryGetProperty("Id", out _));
        Assert.True(internalProcess.RootElement.TryGetProperty("CategoryId", out _));
    }

    private static void AssertResponseSchema(JsonElement root, string path, string method, string status, string schema)
    {
        var operation = Assert.Single(root.GetProperty("paths").EnumerateObject(),
            property => string.Equals(property.Name, path, StringComparison.OrdinalIgnoreCase)).Value.GetProperty(method);
        var result = operation.GetProperty("responses")
            .GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/" + schema, result);
    }

    private static void AssertSourceWire(string actual, object original)
    {
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var expected = Newtonsoft.Json.JsonConvert.SerializeObject(original, settings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)));
        using var wire = JsonDocument.Parse(actual);
        Assert.Empty(wire.RootElement.GetProperty(original is OriginalCategory ? "process" : "order").EnumerateArray());
        Assert.False(wire.RootElement.TryGetProperty("category", out _));
        Assert.False(wire.RootElement.TryGetProperty("Id", out _));
    }

    private static OriginalCategory Original(Category row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        CreatedDate = row.CreatedDate,
        ModifiedDate = row.ModifiedDate,
    };

    private static async Task<Category> SeedAsync(OrderDbContext database)
    {
        var category = new Category { Name = "Existing" };
        var other = new Category { Name = "Unrelated" };
        database.AddRange(category, other);
        await database.SaveChangesAsync();
        database.Processes.AddRange(new Process { CategoryId = category.Id, Name = "Existing A" },
            new Process { CategoryId = category.Id, Name = "Existing B" }, new Process { CategoryId = other.Id, Name = "Unrelated" });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        return category;
    }

    private static async Task<string> SnapshotAsync(OrderDbContext database, int excludedCategory = -1, int excludedProcess = -1) =>
        JsonSerializer.Serialize(new
        {
            Categories = await database.Categories.AsNoTracking().Where(row => row.Id != excludedCategory).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Name, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
            Processes = await database.Processes.AsNoTracking().Where(row => row.Id != excludedProcess).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
        });

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }
}
