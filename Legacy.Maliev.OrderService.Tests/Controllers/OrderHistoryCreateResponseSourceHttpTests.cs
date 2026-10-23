using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalStatus = Maliev.OrderService.Tests.SourceHistoryCreation.OrderStatus;
using OriginalHistory = Maliev.OrderService.Tests.SourceHistoryCreation.OrderStatusHistory;
using OriginalTransition = Maliev.OrderService.Tests.SourceHistoryCreation.OrderStatusHasPossibleStatus;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderHistoryCreateResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const int OrderId = 93003;
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 3, 4, 5, TimeSpan.Zero);
    private static readonly string[] Routes = ["accepted", "cancelled", "declined", "expired", "finished", "InProgress", "new", "paid", "quoted", "rejected", "reopen", "reviewed", "reviewing", "shipped", "generic"];
    public static TheoryData<string, bool> FreshCases
    {
        get
        {
            var rows = new TheoryData<string, bool>();
            foreach (var route in Routes)
                foreach (var nullable in new[] { false, true }) rows.Add(route, nullable);
            return rows;
        }
    }

    [Theory, MemberData(nameof(FreshCases))]
    public async Task FreshCreationReturnsOriginalTrackedStatusGraphUtcDatesAndRouteLocation(string route, bool nullable)
    {
        await using var app = await AppAsync();
        var statusId = await SeedAsync(app, route, nullable);
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.StatusWrite);
        using var response = await client.PostAsync(Route(route, statusId), null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var actual = JsonDocument.Parse(body);
        var id = actual.RootElement.GetProperty("id").GetInt32();
        Assert.NotEqual(OrderId, id);
        Assert.Equal($"/orderstatuses/Histories/{(route == "generic" ? OrderId : id)}", response.Headers.Location!.AbsolutePath);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var stored = Assert.Single(await db.History.AsNoTracking().Where(row => row.OrderId == OrderId).ToArrayAsync());
        Assert.Equal(id, stored.Id);
        Assert.Equal(statusId, stored.OrderStatusId);
        Assert.Equal(Now.UtcDateTime, stored.CreatedDate);
        Assert.Equal(DateTimeKind.Unspecified, stored.CreatedDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, stored.ModifiedDate!.Value.Kind);
        await using var original = SourceContext(db);
        var parent = (await original.Set<OriginalStatus>().FindAsync(statusId))!;
        var item = new OriginalHistory { Id = id, OrderId = OrderId, OrderStatusId = statusId, CreatedDate = Now.UtcDateTime, ModifiedDate = Now.UtcDateTime };
        original.Add(item);
        Assert.Same(parent, item.OrderStatus);
        Assert.Same(item, Assert.Single(parent.OrderStatusHistory));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(item)), JsonNode.Parse(body)));
        Assert.EndsWith("Z", actual.RootElement.GetProperty("createdDate").GetString());
        Assert.EndsWith("Z", actual.RootElement.GetProperty("modifiedDate").GetString());
        var definition = actual.RootElement.GetProperty("orderStatus");
        foreach (var collection in new[] { "orderStatusHistory", "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus" })
            Assert.Empty(definition.GetProperty(collection).EnumerateArray());
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalCreationGraphOmitsOnlyTheReturnedHistoryBackReference(bool nullable)
    {
        var status = new OriginalStatus { Id = 7, Name = "Reviewed", Description = nullable ? null : "รายละเอียด", CreatedDate = nullable ? null : new DateTime(2020, 1, 2), ModifiedDate = nullable ? null : new DateTime(2021, 2, 3) };
        var history = new OriginalHistory { Id = 11, OrderId = OrderId, OrderStatusId = 7, CreatedDate = Now.UtcDateTime, ModifiedDate = Now.UtcDateTime, OrderStatus = status };
        status.OrderStatusHistory.Add(history);
        using var wire = JsonDocument.Parse(SerializeOriginal(history));
        Assert.Equal(new[] { "createdDate", "id", "modifiedDate", "orderId", "orderStatus", "orderStatusId" }, wire.RootElement.EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal));
        var definition = wire.RootElement.GetProperty("orderStatus");
        var required = new[] { "id", "name", "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus", "orderStatusHistory" };
        Assert.Equal((nullable ? required : required.Concat(new[] { "description", "createdDate", "modifiedDate" })).Order(StringComparer.Ordinal), definition.EnumerateObject().Select(row => row.Name).Order(StringComparer.Ordinal));
        Assert.Empty(definition.GetProperty("orderStatusHistory").EnumerateArray());
        Assert.EndsWith("Z", wire.RootElement.GetProperty("createdDate").GetString());
    }

    [Theory]
    [InlineData("reviewed", null, HttpStatusCode.Unauthorized)]
    [InlineData("reviewed", OrderPermissions.StatusRead, HttpStatusCode.Forbidden)]
    [InlineData("generic", null, HttpStatusCode.Unauthorized)]
    [InlineData("generic", OrderPermissions.StatusRead, HttpStatusCode.Forbidden)]
    public async Task CreationAuthorityRejectsWithoutChangingEitherDatabase(string route, string? permission, HttpStatusCode expected)
    {
        await using var app = await AppAsync();
        var id = await SeedAsync(app, route, false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, permission);
        using var response = await client.PostAsync(Route(route, id), null);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("generic")]
    public async Task ExactKeyReplayKeepsOriginalResponseAfterLaterHistoryAndDefinitionChanges(string route)
    {
        await using var app = await AppAsync();
        var id = await SeedAsync(app, route, false);
        using var client = Client(app, OrderPermissions.StatusWrite);
        var key = Guid.NewGuid().ToString("N");
        using var first = await PostAsync(client, Route(route, id), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        Assert.NotNull(JsonNode.Parse(body));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
            var later = new OrderStatus { Name = "Later" };
            statuses.Add(later);
            await statuses.SaveChangesAsync();
            statuses.History.Add(new OrderStatusHistory { OrderId = OrderId, OrderStatusId = later.Id, CreatedDate = new DateTime(2026, 10, 25), ModifiedDate = new DateTime(2026, 10, 25) });
            await statuses.SaveChangesAsync();
            await statuses.Statuses.Where(value => value.Id == id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Description, "changed after acknowledgement"));
        }
        var before = await SnapshotAsync(app);
        using var replay = await PostAsync(client, Route(route, id), key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("generic")]
    public async Task SameStatusDedupReturnsTheRetainedEventWithoutInventingFreshTimestamps(string route)
    {
        await using var app = await AppAsync();
        var id = await SeedAsync(app, route, false);
        int eventId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
            var item = new OrderStatusHistory { OrderId = OrderId, OrderStatusId = id, CreatedDate = new DateTime(2020, 2, 3, 4, 5, 6), ModifiedDate = new DateTime(2021, 3, 4, 5, 6, 7) };
            db.Add(item);
            await db.SaveChangesAsync();
            eventId = item.Id;
        }
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.StatusWrite);
        using var response = await client.PostAsync(Route(route, id), null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(eventId, wire.RootElement.GetProperty("id").GetInt32());
        Assert.Equal("2020-02-03T04:05:06", wire.RootElement.GetProperty("createdDate").GetString());
        Assert.Equal("2021-03-04T05:06:07", wire.RootElement.GetProperty("modifiedDate").GetString());
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("reviewed")]
    [InlineData("generic")]
    public async Task ForbiddenTransitionRetainsConflictAndBothDatabaseSnapshots(string route)
    {
        await using var app = await AppAsync();
        var id = await SeedAsync(app, route, false);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
            var blocked = new OrderStatus { Name = "Blocked predecessor" };
            db.Add(blocked);
            await db.SaveChangesAsync();
            db.History.Add(new OrderStatusHistory { OrderId = OrderId, OrderStatusId = blocked.Id });
            await db.SaveChangesAsync();
        }
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.StatusWrite);
        using var response = await client.PostAsync(Route(route, id), null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Order status transition is not permitted.", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(OrderId, 0)]
    [InlineData(OrderId, 99999)]
    public async Task GenericInvalidIdentifiersPreserveOriginalBadRequestAndNoWrites(int orderId, int statusId)
    {
        await using var app = await AppAsync();
        await SeedAsync(app, "generic", false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, OrderPermissions.StatusWrite);
        using var response = await client.PostAsync($"/orderstatuses/Histories/{orderId}/{statusId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        if (orderId != 0 && statusId != 0) Assert.Equal("Status does not exist", await response.Content.ReadAsStringAsync());
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task AllCreationRoutesAdvertiseTheOriginalCreatedHistoryGraph()
    {
        await using var app = await AppAsync();
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var schema = document["components"]!["schemas"]!["OrderHistoryCreateResponse"]!;
        Assert.NotNull(schema);
        Assert.Equal(new[] { "createdDate", "id", "modifiedDate", "orderId", "orderStatus", "orderStatusId" }, schema["properties"]!.AsObject().Select(value => value.Key).Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "id", "orderId", "orderStatus", "orderStatusId" },
            schema["required"]!.AsArray().Select(value => value!.GetValue<string>()).Order(StringComparer.Ordinal));
        foreach (var name in new[] { "createdDate", "modifiedDate" })
        {
            var types = schema["properties"]![name]!["type"]!.AsArray().Select(value => value!.GetValue<string>());
            Assert.Contains("null", types);
            Assert.Contains("string", types);
        }
        Assert.Equal("#/components/schemas/OrderStatusReadResponse", schema["properties"]!["orderStatus"]!["$ref"]!.GetValue<string>());
        foreach (var route in Routes)
        {
            var template = route == "generic" ? "/orderstatuses/Histories/{orderId}/{statusId}" : $"/orderstatuses/Histories/{{orderId}}/{route}";
            var path = Assert.Single(document["paths"]!.AsObject(), value => value.Key.Equals(template, StringComparison.OrdinalIgnoreCase)).Value!;
            Assert.Equal("#/components/schemas/OrderHistoryCreateResponse", path["post"]!["responses"]!["201"]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
        }
    }

    private Task<WebApplicationFactory<Program>> AppAsync() => fixture.AppAsync(null, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
    });

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(25);
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Route(string route, int id) => $"/orderstatuses/Histories/{OrderId}/{(route == "generic" ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : route)}";

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string route, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<int> SeedAsync(WebApplicationFactory<Program> app, string route, bool nullable)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        orders.Add(new Order { Id = OrderId, Name = "History owner", Process = new Process { Name = "Process", Category = new Category { Name = "Category" } }, Quantity = 3, Manufactured = 1, AllowCancellation = false, LeadTime = null });
        await orders.SaveChangesAsync();
        var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var name = route == "InProgress" ? "In Progress" : route == "generic" ? "Generic source status" : char.ToUpperInvariant(route[0]) + route[1..];
        var target = new OrderStatus { Name = name, Description = nullable ? null : "รายละเอียด source", CreatedDate = new DateTime(2020, 1, 2), ModifiedDate = new DateTime(2021, 2, 3) };
        db.Add(target);
        await db.SaveChangesAsync();
        db.History.Add(new OrderStatusHistory { OrderId = OrderId + 1, OrderStatusId = target.Id, CreatedDate = new DateTime(2019, 1, 1), ModifiedDate = new DateTime(2019, 1, 1) });
        await db.SaveChangesAsync();
        if (nullable) await db.Statuses.Where(row => row.Id == target.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null));
        return target.Id;
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int excludedHistoryId = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Deletions = await orders.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().Where(row => row.Id != excludedHistoryId).OrderBy(row => row.Id).ToArrayAsync(),
        });
    }

    private static string SerializeOriginal(OriginalHistory history)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(history, options.SerializerSettings);
    }

    private static OriginalContext SourceContext(OrderStatusDbContext db) => new(new DbContextOptionsBuilder<OriginalContext>().UseNpgsql(db.Database.GetConnectionString()).Options);
    private sealed class OriginalContext(DbContextOptions<OriginalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Ignore<OriginalTransition>();
            var status = builder.Entity<OriginalStatus>();
            status.ToTable("OrderStatus");
            status.Ignore(row => row.OrderStatusHasPossibleStatusOrderStatus);
            status.Ignore(row => row.OrderStatusHasPossibleStatusPossibleStatus);
            var history = builder.Entity<OriginalHistory>();
            history.ToTable("OrderStatusHistory");
            history.HasOne(row => row.OrderStatus).WithMany(row => row.OrderStatusHistory).HasForeignKey(row => row.OrderStatusId);
            foreach (var entity in builder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.Name.EndsWith("Id", StringComparison.Ordinal)) property.SetColumnName(property.Name[..^2] + "ID");
                    if (property.ClrType == typeof(DateTime?)) property.SetColumnType("timestamp without time zone");
                }
        }
    }
}
