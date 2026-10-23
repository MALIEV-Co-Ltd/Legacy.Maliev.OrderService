using System.Net;
using System.Net.Http.Headers;
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

public sealed class OrderLatestStatusReadSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const int OrderId = 42801;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LatestPreservesSourceDefinitionAndOnlyItsSelectedInverseHistory(bool nullDefinitionFields, bool nullEventDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(statuses, nullDefinitionFields, nullEventDates);
        var before = await SnapshotAsync(statuses);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}/latest", deadline.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        using var actual = JsonDocument.Parse(body);
        Assert.Equal(seed.Status.Id, actual.RootElement.GetProperty("id").GetInt32());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(Original(seed))), JsonNode.Parse(body)));
        var nested = Assert.Single(actual.RootElement.GetProperty("orderStatusHistory").EnumerateArray());
        Assert.Equal(seed.Selected.Id, nested.GetProperty("id").GetInt32());
        Assert.Equal(OrderId, nested.GetProperty("orderId").GetInt32());
        Assert.Equal(seed.Status.Id, nested.GetProperty("orderStatusId").GetInt32());
        Assert.False(nested.TryGetProperty("orderStatus", out _));
        Assert.Equal(new OrderStatusResponse(seed.Status.Id, seed.Status.Name, seed.Status.Description, seed.Status.CreatedDate, seed.Status.ModifiedDate),
            JsonSerializer.Deserialize<OrderStatusResponse>(body, JsonSerializerOptions.Web));
        Assert.Equal(before, await SnapshotAsync(statuses));
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.False(await orders.Orders.AnyAsync());
        Assert.False(await orders.Files.AnyAsync());
        Assert.False(await orders.DeletionIntents.AnyAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData(OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    public async Task LatestRetainsAuthorityAndEmptyResponse(string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}/latest", deadline.Token);
        Assert.Equal(expected, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Empty(await statuses.Statuses.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.History.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.Transitions.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedLifetimeDoesNotDiscloseRetainedSourceGraph(bool completed)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await SeedAsync(statuses, false, false);
        var before = await SnapshotAsync(statuses);
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        orders.DeletionIntents.Add(new OrderDeletionIntent
        {
            OrderId = OrderId,
            DeletionId = Guid.NewGuid(),
            RequestedAtUtc = now,
            NextAttemptAtUtc = now,
            StatusCleanupCompletedAtUtc = completed ? now.AddSeconds(1) : null,
            CompletedAtUtc = completed ? now.AddSeconds(2) : null,
        });
        await orders.SaveChangesAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}/latest", deadline.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("Source latest", await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(before, await SnapshotAsync(statuses));
        Assert.False(await orders.Orders.AnyAsync());
        var receipt = Assert.Single(await orders.DeletionIntents.AsNoTracking().ToArrayAsync());
        Assert.Equal(0, receipt.AttemptCount);
        Assert.Equal(completed, receipt.CompletedAtUtc is not null);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void IsolatedOriginalCyclicGraphSerializesOneInverseHistoryAndOmitsItsBackReference(bool nullDefinitionFields, bool nullEventDates)
    {
        var status = new OriginalStatus
        {
            Id = 51,
            Name = nullDefinitionFields ? null : "Source latest",
            Description = nullDefinitionFields ? null : "Definition",
            CreatedDate = nullDefinitionFields ? null : new DateTime(2018, 1, 1),
            ModifiedDate = nullDefinitionFields ? null : new DateTime(2019, 1, 1),
        };
        status.OrderStatusHistory.Add(new OriginalHistory
        {
            Id = 91,
            OrderId = OrderId,
            OrderStatusId = status.Id,
            CreatedDate = nullEventDates ? null : new DateTime(2020, 1, 1),
            ModifiedDate = nullEventDates ? null : new DateTime(2021, 1, 1),
            OrderStatus = status,
        });
        using var document = JsonDocument.Parse(SerializeOriginal(status));
        var root = document.RootElement;
        var scalarNames = new[] { "name", "description", "createdDate", "modifiedDate" };
        var rootNames = new[] { "id", "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus", "orderStatusHistory" };
        Assert.Equal((nullDefinitionFields ? rootNames : rootNames.Concat(scalarNames)).Order(StringComparer.Ordinal),
            root.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Empty(root.GetProperty("orderStatusHasPossibleStatusOrderStatus").EnumerateArray());
        Assert.Empty(root.GetProperty("orderStatusHasPossibleStatusPossibleStatus").EnumerateArray());
        var history = Assert.Single(root.GetProperty("orderStatusHistory").EnumerateArray());
        var historyNames = new[] { "id", "orderId", "orderStatusId" };
        Assert.Equal((nullEventDates ? historyNames : historyNames.Concat(new[] { "createdDate", "modifiedDate" })).Order(StringComparer.Ordinal),
            history.EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        Assert.Equal(91, history.GetProperty("id").GetInt32());
        Assert.Equal(OrderId, history.GetProperty("orderId").GetInt32());
        Assert.Equal(51, history.GetProperty("orderStatusId").GetInt32());
        Assert.False(history.TryGetProperty("orderStatus", out _));
    }

    [Fact]
    public async Task OriginalLatestIncludeFixesOnlySelectedRowIntoItsUntrackedDefinition()
    {
        await using var app = await fixture.AppAsync(null);
        await using var seedScope = app.Services.CreateAsyncScope();
        var seed = await SeedAsync(seedScope.ServiceProvider.GetRequiredService<OrderStatusDbContext>(), false, false);
        await using var queryScope = app.Services.CreateAsyncScope();
        var statuses = queryScope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var selected = await statuses.History.Include(row => row.OrderStatus).AsNoTracking()
            .Where(row => row.OrderId == OrderId).OrderByDescending(row => row.Id).FirstOrDefaultAsync();
        Assert.NotNull(selected);
        Assert.Equal(seed.Selected.Id, selected.Id);
        Assert.Equal(seed.Status.Id, selected.OrderStatus!.Id);
        Assert.Same(selected, Assert.Single(selected.OrderStatus.History));
        Assert.Empty(selected.OrderStatus.FromTransitions);
        Assert.Empty(selected.OrderStatus.ToTransitions);
        Assert.Empty(statuses.ChangeTracker.Entries());
    }

    [Fact]
    public async Task ServedLatestReadSchemaPreservesSourceNestedHistoryWithoutChangingInternalHistory()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/order/openapi/v1.json", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("OrderLatestStatusReadResponse", out var output));
        Assert.Equal(new[] { "createdDate", "description", "id", "modifiedDate", "name", "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus", "orderStatusHistory" },
            output.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        var history = output.GetProperty("properties").GetProperty("orderStatusHistory");
        foreach (var name in new[] { "orderStatusHasPossibleStatusOrderStatus", "orderStatusHasPossibleStatusPossibleStatus", "orderStatusHistory" })
        {
            var type = output.GetProperty("properties").GetProperty(name).GetProperty("type");
            var types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(value => value.GetString()).ToArray() : [type.GetString()];
            Assert.Contains("array", types);
        }
        Assert.EndsWith("/OrderLatestHistoryReadResponse", history.GetProperty("items").GetProperty("$ref").GetString());
        var nested = schemas.GetProperty("OrderLatestHistoryReadResponse");
        Assert.Equal(new[] { "createdDate", "id", "modifiedDate", "orderId", "orderStatusId" },
            nested.GetProperty("properties").EnumerateObject().Select(value => value.Name).Order(StringComparer.Ordinal));
        foreach (var schema in new[] { output, nested })
        {
            var required = schema.TryGetProperty("required", out var fields) ? fields.EnumerateArray().Select(value => value.GetString()).ToArray() : [];
            Assert.Contains("id", required);
            foreach (var nullable in new[] { "name", "description", "createdDate", "modifiedDate" }) Assert.DoesNotContain(nullable, required);
        }
        foreach (var identifier in new[] { "orderId", "orderStatusId" })
            Assert.Contains(identifier, nested.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var path = document.RootElement.GetProperty("paths").EnumerateObject()
            .Single(value => value.Name.Equals("/orderstatuses/Histories/{orderId}/latest", StringComparison.OrdinalIgnoreCase));
        var routeSchema = path.Value.GetProperty("get").GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.EndsWith("/OrderLatestStatusReadResponse", routeSchema.GetProperty("$ref").GetString());
        Assert.True(schemas.GetProperty("OrderStatusHistoryResponse").GetProperty("properties").TryGetProperty("OrderId", out _));
        Assert.True(schemas.GetProperty("OrderStatusHistoryResponse").GetProperty("properties").TryGetProperty("OrderStatusId", out _));
    }

    private static async Task<Seed> SeedAsync(OrderStatusDbContext statuses, bool nullDefinitionFields, bool nullEventDates)
    {
        var other = new OrderStatus { Name = "Source earlier" };
        var status = new OrderStatus { Name = "Source latest", Description = nullDefinitionFields ? null : "Definition", CreatedDate = new DateTime(2018, 1, 1), ModifiedDate = new DateTime(2019, 1, 1) };
        statuses.AddRange(other, status);
        await statuses.SaveChangesAsync();
        statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = other.Id, PossibleStatusId = status.Id });
        await statuses.SaveChangesAsync();
        var eventYear = 2025;
        foreach (var definition in new[] { other, status, status })
        {
            statuses.History.Add(new OrderStatusHistory { OrderId = OrderId, OrderStatusId = definition.Id, CreatedDate = new DateTime(eventYear--, 1, 1), ModifiedDate = new DateTime(2025, 2, 1) });
            await statuses.SaveChangesAsync();
        }
        var selected = await statuses.History.OrderByDescending(row => row.Id).FirstAsync();
        selected.CreatedDate = new DateTime(2020, 1, 1);
        selected.ModifiedDate = new DateTime(2021, 1, 1);
        await statuses.SaveChangesAsync();
        Assert.True(await statuses.History.AnyAsync(row => row.OrderId == OrderId && row.Id < selected.Id && row.CreatedDate > selected.CreatedDate));
        statuses.History.Add(new OrderStatusHistory { OrderId = OrderId + 1, OrderStatusId = status.Id, CreatedDate = new DateTime(2026, 1, 1) });
        await statuses.SaveChangesAsync();
        if (nullDefinitionFields)
            Assert.Equal(1, await statuses.Statuses.Where(row => row.Id == status.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null)));
        if (nullEventDates)
            Assert.Equal(1, await statuses.History.Where(row => row.Id == selected.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.CreatedDate, (DateTime?)null).SetProperty(row => row.ModifiedDate, (DateTime?)null)));
        await statuses.Entry(status).ReloadAsync();
        await statuses.Entry(selected).ReloadAsync();
        Assert.Equal(3, await statuses.History.CountAsync(row => row.OrderId == OrderId));
        Assert.True(await statuses.History.AnyAsync(row => row.OrderId != OrderId && row.Id > selected.Id));
        return new(status, selected);
    }

    private static async Task<string> SnapshotAsync(OrderStatusDbContext statuses) => JsonSerializer.Serialize(new
    {
        Definitions = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.Name, row.Description, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
        History = await statuses.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
        Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderStatusId, row.PossibleStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync(),
    });

    private static OriginalStatus Original(Seed seed)
    {
        var status = new OriginalStatus { Id = seed.Status.Id, Name = seed.Status.Name, Description = seed.Status.Description, CreatedDate = seed.Status.CreatedDate, ModifiedDate = seed.Status.ModifiedDate };
        status.OrderStatusHistory.Add(new OriginalHistory { Id = seed.Selected.Id, OrderId = seed.Selected.OrderId, OrderStatusId = seed.Selected.OrderStatusId, CreatedDate = seed.Selected.CreatedDate, ModifiedDate = seed.Selected.ModifiedDate, OrderStatus = status });
        return status;
    }

    private static string SerializeOriginal(OriginalStatus status)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(status, options.SerializerSettings);
    }

    private sealed record Seed(OrderStatus Status, OrderStatusHistory Selected);

    // Isolated original CLR model/collection witness; synthetic values, not original-binary execution.
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
