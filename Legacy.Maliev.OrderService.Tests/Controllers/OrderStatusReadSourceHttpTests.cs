using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderStatusReadSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static readonly string[] NavigationNames =
    [
        "orderStatusHasPossibleStatusOrderStatus",
        "orderStatusHasPossibleStatusPossibleStatus",
        "orderStatusHistory",
    ];

    [Theory]
    [InlineData("list", false)]
    [InlineData("list", true)]
    [InlineData("id", false)]
    [InlineData("id", true)]
    [InlineData("name", false)]
    [InlineData("name", true)]
    [InlineData("available", false)]
    [InlineData("available", true)]
    public async Task ReadsPreserveOriginalCamelCaseDefinitionIdsDatesAndEmptyNavigationCollections(string kind, bool nullableFields)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(statuses, nullableFields);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var internalBefore = await service.GetStatusesAsync(default);
        var transitionsBefore = await TransitionsAsync(statuses);
        var expected = kind == "list" ? new[] { seed.Current, seed.Target } : [seed.Target];
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(Route(kind, seed.Current.Id, seed.Target.Id, seed.Target.Name), deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        using var document = JsonDocument.Parse(body);
        var rows = kind is "list" or "available" ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        Assert.Equal(expected.Length, rows.Length);
        foreach (var definition in expected)
        {
            var row = Assert.Single(rows, value => value.GetProperty("id").GetInt32() == definition.Id);
            var original = Original(definition);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(original)), JsonNode.Parse(row.GetRawText())));
            foreach (var name in NavigationNames)
            {
                Assert.Equal(JsonValueKind.Array, row.GetProperty(name).ValueKind);
                Assert.Equal(0, row.GetProperty(name).GetArrayLength());
            }
            // The inspected Intranet reader consumes this same five-field definition DTO.
            Assert.Equal(new OrderStatusResponse(definition.Id, definition.Name, definition.Description, definition.CreatedDate, definition.ModifiedDate),
                JsonSerializer.Deserialize<OrderStatusResponse>(row.GetRawText(), JsonSerializerOptions.Web));
        }
        Assert.Equal(internalBefore, await service.GetStatusesAsync(default));
        Assert.Equal(transitionsBefore, await TransitionsAsync(statuses));
        Assert.Empty(await statuses.History.AsNoTracking().ToArrayAsync());
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.False(await orders.Orders.AnyAsync());
        Assert.False(await orders.Files.AnyAsync());
        Assert.False(await orders.DeletionIntents.AnyAsync());
    }

    [Theory]
    [InlineData("list", null, HttpStatusCode.Unauthorized)]
    [InlineData("id", null, HttpStatusCode.Unauthorized)]
    [InlineData("name", null, HttpStatusCode.Unauthorized)]
    [InlineData("available", null, HttpStatusCode.Unauthorized)]
    [InlineData("list", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData("id", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData("name", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData("available", OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData("list", OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    [InlineData("id", OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    [InlineData("name", OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    [InlineData("available", OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    public async Task ReadsRetainAuthenticationPermissionAndEmptyResponses(string kind, string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync(Route(kind, 42721, 42722, "Absent source status"), deadline.Token);
        Assert.Equal(expected, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Empty(await statuses.Statuses.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.Transitions.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.History.AsNoTracking().ToArrayAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsolatedOriginalModelRetainsInitializedCollectionsAndNewtonsoftNullOmission(bool nullableFields)
    {
        var original = new OriginalStatus
        {
            Id = 51,
            Name = nullableFields ? null : "Source status",
            Description = nullableFields ? null : "Source description",
            CreatedDate = nullableFields ? null : new DateTime(2020, 1, 2),
            ModifiedDate = nullableFields ? null : new DateTime(2021, 2, 3),
        };
        using var document = JsonDocument.Parse(SerializeOriginal(original));
        var expected = NavigationNames.Append("id");
        if (!nullableFields) expected = expected.Concat(new[] { "name", "description", "createdDate", "modifiedDate" });
        Assert.Equal(expected.Order(StringComparer.Ordinal), document.RootElement.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        foreach (var name in NavigationNames) Assert.Equal(0, document.RootElement.GetProperty(name).GetArrayLength());
        Assert.Equal(!nullableFields, document.RootElement.TryGetProperty("name", out _));
        Assert.Equal(!nullableFields, document.RootElement.TryGetProperty("description", out _));
        Assert.Equal(!nullableFields, document.RootElement.TryGetProperty("createdDate", out _));
        Assert.Equal(!nullableFields, document.RootElement.TryGetProperty("modifiedDate", out _));
    }

    [Fact]
    public async Task AvailableSourceQueryReloadsFreshDefinitionsAfterItsUntrackedIncludes()
    {
        await using var app = await fixture.AppAsync(null);
        await using var seedScope = app.Services.CreateAsyncScope();
        var seed = await SeedAsync(seedScope.ServiceProvider.GetRequiredService<OrderStatusDbContext>(), false);
        await using var queryScope = app.Services.CreateAsyncScope();
        var statuses = queryScope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        // The original controller includes an untracked transition graph, then returns FindAsync results.
        var included = await statuses.Transitions.Include(row => row.OrderStatus).Include(row => row.PossibleStatus)
            .AsNoTracking().Where(row => row.OrderStatusId == seed.Current.Id).ToListAsync();
        Assert.Empty(statuses.ChangeTracker.Entries());
        var selected = Assert.Single(included).PossibleStatus!;
        var compact = await statuses.Statuses.FindAsync(selected.Id);
        Assert.NotNull(compact);
        Assert.NotSame(selected, compact);
        Assert.Empty(compact.FromTransitions);
        Assert.Empty(compact.ToTransitions);
        Assert.Empty(compact.History);
        Assert.Equal(seed.Target.Id, compact.Id);
    }

    [Fact]
    public async Task ServedDefinitionReadSchemasExposeOriginalNamesWithoutChangingInternalHistory()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/order/openapi/v1.json", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("OrderStatusReadResponse", out var output));
        var expectedNames = NavigationNames.Concat(new[] { "id", "name", "description", "createdDate", "modifiedDate" });
        Assert.Equal(expectedNames.Order(StringComparer.Ordinal), output.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        foreach (var name in NavigationNames)
            Assert.Equal("array", output.GetProperty("properties").GetProperty(name).GetProperty("type").GetString());
        var required = output.TryGetProperty("required", out var requiredFields)
            ? requiredFields.EnumerateArray().Select(value => value.GetString()).ToArray() : [];
        Assert.Contains("id", required);
        foreach (var name in new[] { "name", "description", "createdDate", "modifiedDate" }) Assert.DoesNotContain(name, required);
        foreach (var (route, array) in new[]
        {
            ("/orderstatuses", true),
            ("/orderstatuses/{id}", false),
            ("/orderstatuses/{statusName}", false),
            ("/orderstatuses/{currentStatusId}/available", true),
        })
        {
            var path = document.RootElement.GetProperty("paths").EnumerateObject().Single(value => value.Name.Equals(route, StringComparison.OrdinalIgnoreCase));
            var schema = path.Value.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
            Assert.EndsWith("/OrderStatusReadResponse", (array ? schema.GetProperty("items") : schema).GetProperty("$ref").GetString());
        }
        Assert.True(schemas.GetProperty("OrderStatusHistoryResponse").GetProperty("properties").TryGetProperty("OrderId", out _));
        Assert.True(schemas.GetProperty("OrderStatusHistoryResponse").GetProperty("properties").TryGetProperty("OrderStatusId", out _));
    }

    private static string Route(string kind, int currentId, int targetId, string? name) => kind switch
    {
        "list" => "/orderstatuses",
        "id" => $"/orderstatuses/{targetId}",
        "name" => $"/orderstatuses/{Uri.EscapeDataString(name!)}",
        "available" => $"/orderstatuses/{currentId}/available",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string SerializeOriginal(OriginalStatus status)
    {
        var original = new MvcNewtonsoftJsonOptions();
        original.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        original.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(status, original.SerializerSettings);
    }

    private static OriginalStatus Original(OrderStatus status) => new()
    {
        Id = status.Id,
        Name = status.Name,
        Description = status.Description,
        CreatedDate = status.CreatedDate,
        ModifiedDate = status.ModifiedDate,
    };

    private static async Task<Seed> SeedAsync(OrderStatusDbContext statuses, bool nullableFields)
    {
        var current = new OrderStatus { Name = "Source current", CreatedDate = new DateTime(2011, 1, 2), ModifiedDate = new DateTime(2012, 1, 2) };
        var target = new OrderStatus
        {
            Name = "Source target",
            Description = nullableFields ? null : "Retained source description",
            CreatedDate = new DateTime(2018, 1, 2),
            ModifiedDate = new DateTime(2019, 1, 2),
        };
        statuses.AddRange(current, target);
        await statuses.SaveChangesAsync();
        statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = current.Id, PossibleStatusId = target.Id });
        await statuses.SaveChangesAsync();
        if (nullableFields)
        {
            Assert.Equal(1, await statuses.Statuses.Where(row => row.Id == target.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null)));
            await statuses.Entry(target).ReloadAsync();
        }
        return new(current, target);
    }

    private static Task<TransitionSnapshot[]> TransitionsAsync(OrderStatusDbContext statuses) => statuses.Transitions.AsNoTracking().OrderBy(row => row.Id)
        .Select(row => new TransitionSnapshot(row.Id, row.OrderStatusId, row.PossibleStatusId, row.CreatedDate, row.ModifiedDate)).ToArrayAsync();

    private sealed record Seed(OrderStatus Current, OrderStatus Target);
    private sealed record TransitionSnapshot(int Id, int OrderStatusId, int PossibleStatusId, DateTime? CreatedDate, DateTime? ModifiedDate);

    // Isolated source model witness: original CLR members/initialized collections, synthetic values only.
    private sealed class OriginalStatus
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public ICollection<OriginalTransition> OrderStatusHasPossibleStatusOrderStatus { get; set; } = new HashSet<OriginalTransition>();
        public ICollection<OriginalTransition> OrderStatusHasPossibleStatusPossibleStatus { get; set; } = new HashSet<OriginalTransition>();
        public ICollection<OriginalHistory> OrderStatusHistory { get; set; } = new HashSet<OriginalHistory>();
    }

    private sealed class OriginalTransition
    {
        public int Id { get; set; }
        public int OrderStatusId { get; set; }
        public int PossibleStatusId { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public OriginalStatus? OrderStatus { get; set; }
        public OriginalStatus? PossibleStatus { get; set; }
    }

    private sealed class OriginalHistory
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int OrderStatusId { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public OriginalStatus? OrderStatus { get; set; }
    }
}
