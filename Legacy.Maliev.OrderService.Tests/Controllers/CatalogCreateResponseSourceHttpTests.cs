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
using OriginalFileFormat = Maliev.OrderService.Data.Database.OrderContext.FileFormat;
using OriginalStatus = Maliev.OrderStatusService.Data.Database.OrderStatusContext.OrderStatus;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class CatalogCreateResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("status", 0)]
    [InlineData("status", 1)]
    [InlineData("format", 0)]
    [InlineData("format", 1)]
    [InlineData("format", 2)]
    [InlineData("format", 3)]
    public async Task CreateReturnsOriginalFreshUtcEntityWhileLocationRetainsStoredDates(string kind, int nullFields)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await SeedAsync(orders, statuses, deadline.Token);
        var before = await SnapshotAsync(orders, statuses, deadline.Token);
        var name = kind == "format" && (nullFields & 1) != 0 ? null : "  ไทย / created  ";
        var text = (nullFields & (kind == "status" ? 1 : 2)) != 0 ? null : "  ไทย .STEP  ";
        var input = new JsonObject
        {
            ["Id"] = 99999999,
            ["Name"] = name,
            [kind == "status" ? "Description" : "Extension"] = text,
            ["CreatedDate"] = "2000-01-01T00:00:00Z",
            ["ModifiedDate"] = "2000-01-02T00:00:00Z",
        };
        if (kind == "status")
        {
            foreach (var navigation in NavigationNames)
                input[navigation] = new JsonArray(new JsonObject { ["Id"] = 77777, ["OrderStatusId"] = 99999999 });
        }
        using var writer = Client(app, kind == "status" ? OrderPermissions.StatusWrite : OrderPermissions.CatalogWrite);
        using var payload = new StringContent(input.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await writer.PostAsync(Route(kind), payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        using var consumerBody = new StringContent(body, Encoding.UTF8, "application/json");
        var id = kind == "status"
            ? (await consumerBody.ReadFromJsonAsync<OrderStatusResponse>(deadline.Token))!.Id
            : (await consumerBody.ReadFromJsonAsync<FileFormatResponse>(deadline.Token))!.Id;
        Assert.NotEqual(99999999, id);
        var utc = clock.GetUtcNow().UtcDateTime;
        object original;
        object storedOriginal;
        if (kind == "status")
        {
            var stored = await statuses.Statuses.AsNoTracking().SingleAsync(row => row.Id == id, deadline.Token);
            Assert.Equal(name, stored.Name);
            Assert.Equal(text, stored.Description);
            AssertStoredDates(stored.CreatedDate, stored.ModifiedDate, utc);
            original = new OriginalStatus { Id = id, Name = name, Description = text, CreatedDate = utc, ModifiedDate = utc };
            storedOriginal = new OriginalStatus { Id = id, Name = stored.Name, Description = stored.Description, CreatedDate = stored.CreatedDate, ModifiedDate = stored.ModifiedDate };
            Assert.Equal(before, await SnapshotAsync(orders, statuses, deadline.Token, excludedStatus: id));
        }
        else
        {
            var stored = await orders.FileFormats.AsNoTracking().SingleAsync(row => row.Id == id, deadline.Token);
            Assert.Equal(name, stored.Name);
            Assert.Equal(text, stored.Extension);
            AssertStoredDates(stored.CreatedDate, stored.ModifiedDate, utc);
            original = new OriginalFileFormat { Id = id, Name = name, Extension = text, CreatedDate = utc, ModifiedDate = utc };
            storedOriginal = new OriginalFileFormat { Id = id, Name = stored.Name, Extension = stored.Extension, CreatedDate = stored.CreatedDate, ModifiedDate = stored.ModifiedDate };
            Assert.Equal(before, await SnapshotAsync(orders, statuses, deadline.Token, excludedFormat: id));
        }
        AssertSourceWire(body, original);
        using var postWire = JsonDocument.Parse(body);
        Assert.Equal("2026-10-24T01:02:03Z", postWire.RootElement.GetProperty("createdDate").GetString());
        Assert.Equal("2026-10-24T01:02:03Z", postWire.RootElement.GetProperty("modifiedDate").GetString());
        if (kind == "status")
            foreach (var navigation in NavigationNames) Assert.Empty(postWire.RootElement.GetProperty(navigation).EnumerateArray());
        using var reader = Client(app, kind == "status" ? OrderPermissions.StatusRead : OrderPermissions.CatalogRead);
        using var location = await reader.GetAsync(response.Headers.Location, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        var locationBody = await location.Content.ReadAsStringAsync(deadline.Token);
        AssertSourceWire(locationBody, storedOriginal);
        using var getWire = JsonDocument.Parse(locationBody);
        Assert.Equal("2026-10-24T01:02:03", getWire.RootElement.GetProperty("createdDate").GetString());
        Assert.Equal("2026-10-24T01:02:03", getWire.RootElement.GetProperty("modifiedDate").GetString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("format")]
    public void OriginalCreationOracleDistinguishesUtcFromStoredDates(string kind)
    {
        var utc = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc);
        var stored = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);
        object original = kind == "status"
            ? new OriginalStatus { Id = 17, Name = "Control", CreatedDate = utc, ModifiedDate = utc }
            : new OriginalFileFormat { Id = 17, CreatedDate = utc, ModifiedDate = utc };
        object reloaded = kind == "status"
            ? new OriginalStatus { Id = 17, Name = "Control", CreatedDate = stored, ModifiedDate = stored }
            : new OriginalFileFormat { Id = 17, CreatedDate = stored, ModifiedDate = stored };
        using var fresh = JsonDocument.Parse(SerializeOriginal(original));
        using var find = JsonDocument.Parse(SerializeOriginal(reloaded));
        Assert.Equal("2026-10-24T01:02:03Z", fresh.RootElement.GetProperty("createdDate").GetString());
        Assert.Equal("2026-10-24T01:02:03", find.RootElement.GetProperty("createdDate").GetString());
        Assert.False(fresh.RootElement.TryGetProperty(kind == "status" ? "description" : "extension", out _));
        Assert.False(fresh.RootElement.TryGetProperty("Id", out _));
        Assert.False(JsonNode.DeepEquals(JsonNode.Parse(fresh.RootElement.GetRawText()), JsonNode.Parse(find.RootElement.GetRawText())));
        if (original is OriginalStatus status)
        {
            Assert.IsType<HashSet<Maliev.OrderStatusService.Data.Database.OrderStatusContext.OrderStatusHistory>>(status.OrderStatusHistory);
            foreach (var navigation in NavigationNames) Assert.Empty(fresh.RootElement.GetProperty(navigation).EnumerateArray());
        }
    }

    [Theory]
    [InlineData("status")]
    [InlineData("format")]
    public async Task UnauthorizedAndWrongPermissionCreatesLeaveBothDatabasesUnchanged(string kind)
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await SeedAsync(orders, statuses, deadline.Token);
        var before = await SnapshotAsync(orders, statuses, deadline.Token);
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized),
            (kind == "status" ? OrderPermissions.StatusRead : OrderPermissions.CatalogRead, HttpStatusCode.Forbidden),
        })
        {
            using var client = Client(app, permission);
            using var body = new StringContent("{\"Name\":\"Rejected\"}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(Route(kind), body, deadline.Token);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(orders, statuses, deadline.Token));
    }

    [Fact]
    public async Task ServedCreateSchemasRetainOriginalNamesAndInternalRequestContracts()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/order/openapi/v1.json", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var root = document.RootElement;
        foreach (var (path, type) in new[] { ("/orderstatuses", "OrderStatusReadResponse"), ("/orders/FileFormats", "FileFormatResponse") })
        {
            var operation = Assert.Single(root.GetProperty("paths").EnumerateObject(), row => row.Name.Equals(path, StringComparison.OrdinalIgnoreCase)).Value.GetProperty("post");
            Assert.Equal("#/components/schemas/" + type, operation.GetProperty("responses").GetProperty("201")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.Equal(new[] { "Description", "Name" }, schemas.GetProperty("UpsertOrderStatusRequest").GetProperty("properties").EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Extension", "Name" }, schemas.GetProperty("UpsertFileFormatRequest").GetProperty("properties").EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal));
        using var internalStatus = JsonDocument.Parse(JsonSerializer.Serialize(new OrderStatusResponse(17, "Control", null, null, null)));
        Assert.True(internalStatus.RootElement.TryGetProperty("Id", out _));
        Assert.False(internalStatus.RootElement.TryGetProperty("id", out _));
    }

    private static readonly string[] NavigationNames =
    [
        "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus", "orderStatusHistory",
    ];

    private static void AssertStoredDates(DateTime? created, DateTime? modified, DateTime utc)
    {
        Assert.Equal(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), created);
        Assert.Equal(DateTime.SpecifyKind(utc, DateTimeKind.Unspecified), modified);
        Assert.Equal(DateTimeKind.Unspecified, created!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, modified!.Value.Kind);
    }

    private static string SerializeOriginal(object original)
    {
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(original, settings);
    }

    private static void AssertSourceWire(string actual, object original) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(original)), JsonNode.Parse(actual)));

    private static async Task SeedAsync(OrderDbContext orders, OrderStatusDbContext statuses, CancellationToken token)
    {
        orders.FileFormats.Add(new FileFormat { Name = "Existing", Extension = ".old" });
        statuses.Statuses.Add(new OrderStatus { Name = "Existing", Description = "Unrelated" });
        await orders.SaveChangesAsync(token);
        await statuses.SaveChangesAsync(token);
        orders.ChangeTracker.Clear();
        statuses.ChangeTracker.Clear();
    }

    private static async Task<string> SnapshotAsync(OrderDbContext orders, OrderStatusDbContext statuses, CancellationToken token, int excludedStatus = -1, int excludedFormat = -1) =>
        JsonSerializer.Serialize(new
        {
            Formats = await orders.FileFormats.AsNoTracking().Where(row => row.Id != excludedFormat).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Name, row.Extension, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking().Where(row => row.Id != excludedStatus).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.Name, row.Description, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.OrderStatusId, row.PossibleStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking().OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(token),
            Orders = await orders.Orders.CountAsync(token),
            Files = await orders.Files.CountAsync(token),
            DeletionIntents = await orders.DeletionIntents.CountAsync(token),
        });

    private static string Route(string kind) => kind == "status" ? "/orderstatuses" : "/orders/fileformats";

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }
}
