using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalCategory = Maliev.OrderService.Data.Database.OrderContext.Category;
using OriginalProcess = Maliev.OrderService.Data.Database.OrderContext.Process;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class ProcessReclassificationSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const string RenamedCategory = "Reclassified category";
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 6, 7, 8, TimeSpan.Zero);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReclassificationAndCategoryRenameChangeOnlySourceFieldsAndReadMembership(bool reverse, bool nullCreated)
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = await AppAsync(clock);
        var seed = await SeedAsync(app, reverse, nullCreated);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        await AssertReadsAsync(app, reader, seed);
        var before = await SnapshotAsync(app, excludedProcess: seed.ProcessId);
        using (var response = await PutAsync(writer, $"/orders/processes/{seed.ProcessId}", ProcessBody(seed)))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.Location);
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var process = await db.Processes.AsNoTracking().SingleAsync(row => row.Id == seed.ProcessId);
            Assert.Equal(seed.DestinationCategory, process.CategoryId);
            Assert.Equal("Moved process", process.Name);
            Assert.Equal(seed.Created, process.CreatedDate);
            Assert.Equal(Now.UtcDateTime, process.ModifiedDate);
        }
        Assert.Equal(before, await SnapshotAsync(app, excludedProcess: seed.ProcessId));
        var moved = await AssertReadsAsync(app, reader, seed);
        Assert.DoesNotContain(seed.ProcessId, moved[seed.SourceLabel]);
        Assert.Equal(new[] { seed.ProcessId, seed.DestinationNeighbor }.Order(), moved[seed.DestinationLabel].Order());
        var afterMove = await SnapshotAsync(app, excludedCategory: seed.DestinationCategory);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (var response = await PutAsync(writer, $"/orders/categories/{seed.DestinationCategory}", CategoryBody(seed)))
        {
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
            Assert.Null(response.Headers.Location);
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var category = await db.Categories.AsNoTracking().SingleAsync(row => row.Id == seed.DestinationCategory);
            Assert.Equal(RenamedCategory, category.Name);
            Assert.Equal(seed.Created, category.CreatedDate);
            Assert.Equal(Now.AddSeconds(1).UtcDateTime, category.ModifiedDate);
        }
        Assert.Equal(afterMove, await SnapshotAsync(app, excludedCategory: seed.DestinationCategory));
        var renamed = await AssertReadsAsync(app, reader, seed);
        Assert.Empty(renamed[seed.DestinationLabel]);
        Assert.Equal(new[] { seed.SourceNeighbor }, renamed[seed.SourceLabel]);
        Assert.Equal(4, renamed["All"].Length);
    }

    [Theory]
    [InlineData("process", null, HttpStatusCode.Unauthorized)]
    [InlineData("process", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData("category", null, HttpStatusCode.Unauthorized)]
    [InlineData("category", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    public async Task BothWritesRetainExistingAuthorityAndLeaveAllNeighborsUnchanged(string kind, string? permission, HttpStatusCode expected)
    {
        await using var app = await AppAsync(new FakeTimeProvider(Now));
        var seed = await SeedAsync(app, false, false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, permission);
        using var response = await PutAsync(client, Route(kind, seed), kind == "process" ? ProcessBody(seed) : CategoryBody(seed));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("category")]
    public async Task MissingRouteIdDoesNotUseTheValidBodyIdOrChangeMembership(string kind)
    {
        await using var app = await AppAsync(new FakeTimeProvider(Now));
        var seed = await SeedAsync(app, false, false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.CatalogWrite);
        using var response = await PutAsync(client, $"/orders/{(kind == "process" ? "processes" : "categories")}/{int.MaxValue}", kind == "process" ? ProcessBody(seed) : CategoryBody(seed));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("process")]
    [InlineData("category")]
    public async Task NullBodyCannotChangeAValidRouteTarget(string kind)
    {
        await using var app = await AppAsync(new FakeTimeProvider(Now));
        var seed = await SeedAsync(app, false, false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.CatalogWrite);
        using var response = await PutAsync(client, Route(kind, seed), "null");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalPutAssignmentsPreserveIdentityCreationAndUnloadedCollections(bool nullCreated)
    {
        var created = nullCreated ? (DateTime?)null : new DateTime(2018, 1, 2);
        var process = new OriginalProcess { Id = 11, CategoryId = 1, Name = "Moved process", CreatedDate = created };
        var category = new OriginalCategory { Id = 2, Name = "Scanning", CreatedDate = created };
        var processInput = Newtonsoft.Json.JsonConvert.DeserializeObject<OriginalProcess>("{\"id\":999,\"categoryId\":2,\"name\":\"Moved process\",\"createdDate\":\"2099-01-01\",\"category\":{\"id\":999,\"name\":\"Ignored\"}}")!;
        var categoryInput = Newtonsoft.Json.JsonConvert.DeserializeObject<OriginalCategory>("{\"id\":999,\"name\":\"Reclassified category\",\"createdDate\":\"2099-01-01\",\"process\":[{\"id\":999,\"name\":\"Ignored\"}]}")!;
        // Source PUT assignments; no Legacy mapper, request DTO or persistence method is invoked.
        process.ModifiedDate = Now.UtcDateTime;
        process.Name = processInput.Name;
        process.CategoryId = processInput.CategoryId;
        category.Name = categoryInput.Name;
        category.ModifiedDate = Now.AddSeconds(1).UtcDateTime;
        Assert.Equal(11, process.Id);
        Assert.Equal(2, category.Id);
        Assert.Equal(2, process.CategoryId);
        Assert.Equal(RenamedCategory, category.Name);
        Assert.Equal(created, process.CreatedDate);
        Assert.Equal(created, category.CreatedDate);
        Assert.Null(process.Category);
        Assert.Empty(process.Order);
        Assert.Empty(category.Process);
    }

    private Task<WebApplicationFactory<Program>> AppAsync(TimeProvider clock) => fixture.AppAsync(null, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
    });

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(25);
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Route(string kind, Seed seed) => kind == "process" ? $"/orders/processes/{seed.ProcessId}" : $"/orders/categories/{seed.DestinationCategory}";
    private static string ProcessBody(Seed seed) => JsonSerializer.Serialize(new
    {
        Id = seed.SourceNeighbor,
        CategoryId = seed.DestinationCategory,
        Name = "Moved process",
        CreatedDate = "2099-01-01T00:00:00Z",
        ModifiedDate = "2099-02-02T00:00:00Z",
        Category = new { Id = seed.SourceCategory, Name = "Ignored category" },
    });
    private static string CategoryBody(Seed seed) => JsonSerializer.Serialize(new
    {
        Id = seed.SourceCategory,
        Name = RenamedCategory,
        CreatedDate = "2099-01-01T00:00:00Z",
        ModifiedDate = "2099-02-02T00:00:00Z",
        Process = new[] { new { Id = seed.SourceNeighbor, CategoryId = seed.DestinationCategory, Name = "Ignored process" } },
    });
    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string route, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, route) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        return await client.SendAsync(request);
    }

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> app, bool reverse, bool nullCreated)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var created = nullCreated ? (DateTime?)null : new DateTime(2018, 1, 2);
        var source = new Category { Name = reverse ? "Scanning" : "Additive", CreatedDate = created, ModifiedDate = new DateTime(2019, 1, 1) };
        var destination = new Category { Name = reverse ? "Additive" : "Scanning", CreatedDate = created, ModifiedDate = new DateTime(2019, 1, 1) };
        var foreign = new Category { Name = "Other", CreatedDate = new DateTime(2017, 1, 1), ModifiedDate = new DateTime(2017, 2, 1) };
        db.AddRange(source, destination, foreign);
        await db.SaveChangesAsync();
        var process = new Process { CategoryId = source.Id, Name = "Moved process", CreatedDate = created, ModifiedDate = new DateTime(2019, 1, 1) };
        var sourceNeighbor = new Process { CategoryId = source.Id, Name = "Source neighbor", CreatedDate = new DateTime(2020, 1, 1), ModifiedDate = new DateTime(2021, 1, 1) };
        var destinationNeighbor = new Process { CategoryId = destination.Id, Name = "Destination neighbor", CreatedDate = new DateTime(2020, 2, 1), ModifiedDate = new DateTime(2021, 2, 1) };
        var foreignProcess = new Process { CategoryId = foreign.Id, Name = "Foreign neighbor" };
        db.AddRange(process, sourceNeighbor, destinationNeighbor, foreignProcess);
        await db.SaveChangesAsync();
        db.Orders.Add(new Order { Name = "Unchanged dependent order", ProcessId = process.Id, Quantity = 3, Manufactured = 1 });
        db.FileFormats.Add(new FileFormat { Name = "Unchanged format", Extension = ".txt" });
        await db.SaveChangesAsync();
        if (nullCreated)
        {
            await db.Processes.Where(row => row.Id == process.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null));
            await db.Categories.Where(row => row.Id == source.Id || row.Id == destination.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null));
        }
        return new(process.Id, source.Id, destination.Id, sourceNeighbor.Id, destinationNeighbor.Id, source.Name!, destination.Name!, created);
    }

    private static async Task<Dictionary<string, int[]>> AssertReadsAsync(WebApplicationFactory<Program> app, HttpClient reader, Seed seed)
    {
        var before = await SnapshotAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processes = await db.Processes.AsNoTracking().ToArrayAsync();
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(row => row.Id);
        var membership = new Dictionary<string, int[]>();
        foreach (var label in new[] { "Additive", "Scanning", "All" })
        {
            var selected = label == "All" ? processes : processes.Where(row => categories[row.CategoryId].Name == label).ToArray();
            membership[label] = selected.Select(row => row.Id).ToArray();
            using var response = await reader.GetAsync(label == "All" ? "/orders/processes" : $"/orders/processes/{label.ToLowerInvariant()}");
            if (selected.Length == 0)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                continue;
            }
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            var expected = selected.Select(row => Original(row, label == "All" ? null : categories[row.CategoryId])).ToArray();
            Assert.True(JsonNode.DeepEquals(SortedArray(SerializeOriginal(expected)), SortedArray(body)));
            // Active Intranet BFF consumes id/name with Web defaults; this is JSON compatibility, not a BFF-host execution.
            var consumer = JsonSerializer.Deserialize<ProcessItem[]>(body, JsonSerializerOptions.Web)!;
            Assert.Equal(selected.OrderBy(row => row.Id).Select(row => new ProcessItem(row.Id, row.Name)), consumer.OrderBy(row => row.Id));
        }
        using (var response = await reader.GetAsync($"/orders/processes/{seed.ProcessId}"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var process = Assert.Single(processes, row => row.Id == seed.ProcessId);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(Original(process, null))), JsonNode.Parse(await response.Content.ReadAsStringAsync())));
        }
        foreach (var id in new[] { seed.SourceCategory, seed.DestinationCategory })
        {
            using var response = await reader.GetAsync($"/orders/categories/{id}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var category = categories[id];
            var expected = new OriginalCategory { Id = id, Name = category.Name!, CreatedDate = category.CreatedDate, ModifiedDate = category.ModifiedDate };
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(expected)), JsonNode.Parse(await response.Content.ReadAsStringAsync())));
        }
        Assert.Equal(before, await SnapshotAsync(app));
        return membership;
    }

    private static OriginalProcess Original(Process value, Category? category)
    {
        var process = new OriginalProcess { Id = value.Id, CategoryId = value.CategoryId, Name = value.Name, CreatedDate = value.CreatedDate, ModifiedDate = value.ModifiedDate };
        if (category is not null)
        {
            var parent = new OriginalCategory { Id = category.Id, Name = category.Name!, CreatedDate = category.CreatedDate, ModifiedDate = category.ModifiedDate };
            parent.Process.Add(process);
            process.Category = parent;
        }
        return process;
    }

    private static JsonArray SortedArray(string body) => new(JsonNode.Parse(body)!.AsArray().OrderBy(row => row!["id"]!.GetValue<int>()).Select(row => row!.DeepClone()).ToArray());
    private static string SerializeOriginal(object value)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, options.SerializerSettings);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int excludedProcess = 0, int excludedCategory = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().Where(row => row.Id != excludedProcess).OrderBy(row => row.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().Where(row => row.Id != excludedCategory).OrderBy(row => row.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Deletions = await orders.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
        });
    }

    private sealed record Seed(int ProcessId, int SourceCategory, int DestinationCategory, int SourceNeighbor, int DestinationNeighbor, string SourceLabel, string DestinationLabel, DateTime? Created);
    private sealed record ProcessItem(int Id, string Name);
}
