using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderHistoryReadSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const int OrderId = 42711;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryPreservesSourceShapeOrdinalsEventDatesAndRepeatedStatuses(bool omitNullableFields)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(statuses);
        if (omitNullableFields)
            await statuses.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"OrderStatusHistory\" SET \"ModifiedDate\" = NULL WHERE \"OrderID\" = {OrderId}");
        var definitionsBefore = await DefinitionsAsync(statuses);
        var historyBefore = await HistoryAsync(statuses);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var internalBefore = await service.GetHistoryAsync(OrderId, default);
        Assert.Equal(3, internalBefore.Count);
        Assert.All(internalBefore, row => Assert.True(row.Id > 1));
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        var expected = new[]
        {
            new OrderStatusResponse(1, seed.First.Name, seed.First.Description, seed.Early.CreatedDate,
                omitNullableFields ? null : seed.Early.ModifiedDate),
            new OrderStatusResponse(2, seed.Second.Name, seed.Second.Description, seed.Middle.CreatedDate,
                omitNullableFields ? null : seed.Middle.ModifiedDate),
            new OrderStatusResponse(3, seed.First.Name, seed.First.Description, seed.Late.CreatedDate,
                omitNullableFields ? null : seed.Late.ModifiedDate),
        };
        Assert.Equal(expected, JsonSerializer.Deserialize<OrderStatusResponse[]>(body, JsonSerializerOptions.Web));
        using var document = JsonDocument.Parse(body);
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var fields = new List<string> { "id", "name", "createdDate" };
            if (row.GetProperty("name").GetString() == seed.Second.Name) fields.Add("description");
            if (!omitNullableFields) fields.Add("modifiedDate");
            Assert.Equal(fields.Order(StringComparer.Ordinal), row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        }
        for (var index = 0; index < expected.Length; index++) AssertSourceWire(document.RootElement[index], expected[index]);
        // The inspected Intranet reader has this same seven-field DTO and uses Name/CreatedDate only.
        var consumer = JsonSerializer.Deserialize<OrderStatusHistoryResponse[]>(body, JsonSerializerOptions.Web)!;
        Assert.Equal(expected.Select(row => row.Name), consumer.Select(row => row.Name));
        Assert.Equal(expected.Select(row => row.CreatedDate), consumer.Select(row => row.CreatedDate));
        Assert.All(consumer, row => { Assert.Equal(0, row.OrderId); Assert.Equal(0, row.OrderStatusId); });
        Assert.Equal(internalBefore, await service.GetHistoryAsync(OrderId, default));
        Assert.Equal(definitionsBefore, await DefinitionsAsync(statuses));
        Assert.Equal(historyBefore, await HistoryAsync(statuses));
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.False(await orders.Orders.AnyAsync());
        Assert.False(await orders.Files.AnyAsync());
        Assert.False(await orders.DeletionIntents.AnyAsync());
    }

    [Fact]
    public async Task HistoryRetainsNullCreatedDatesAndSourceChronologyWithoutChangingStoredIds()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var seed = await SeedAsync(statuses);
        await statuses.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"OrderStatusHistory\" SET \"CreatedDate\" = NULL WHERE \"ID\" = {seed.Late.Id}");
        var historyBefore = await HistoryAsync(statuses);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        Assert.Equal(new[]
        {
            new OrderStatusResponse(1, seed.First.Name, null, null, seed.Late.ModifiedDate),
            new OrderStatusResponse(2, seed.First.Name, null, seed.Early.CreatedDate, seed.Early.ModifiedDate),
            new OrderStatusResponse(3, seed.Second.Name, seed.Second.Description, seed.Middle.CreatedDate, seed.Middle.ModifiedDate),
        }, JsonSerializer.Deserialize<OrderStatusResponse[]>(body, JsonSerializerOptions.Web));
        using var document = JsonDocument.Parse(body);
        Assert.False(document.RootElement[0].TryGetProperty("createdDate", out _));
        Assert.Equal(new[] { "id", "modifiedDate", "name" }, document.RootElement[0]
            .EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        AssertSourceWire(document.RootElement[0], new OrderStatusResponse(1, seed.First.Name, null, null, seed.Late.ModifiedDate));
        Assert.Equal(historyBefore, await HistoryAsync(statuses));
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData(OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    public async Task HistoryPreservesAuthenticationPermissionAndEmptyResponses(string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        using var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync($"/orderstatuses/Histories/{OrderId}", deadline.Token);
        Assert.Equal(expected, response.StatusCode);
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Empty(await statuses.Statuses.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.History.AsNoTracking().ToArrayAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }

    [Fact]
    public async Task ServedHistorySchemaKeepsLegacyNamesOptionalNullsAndSeparateInternalHistory()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/order/openapi/v1.json", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("OrderHistoryStatusResponse", out var output));
        Assert.Equal(new[] { "createdDate", "description", "id", "modifiedDate", "name" },
            output.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var required = output.TryGetProperty("required", out var requiredFields)
            ? requiredFields.EnumerateArray().Select(value => value.GetString()).ToArray() : [];
        Assert.Contains("id", required);
        foreach (var optional in new[] { "name", "description", "createdDate", "modifiedDate" })
            Assert.DoesNotContain(optional, required);
        var path = document.RootElement.GetProperty("paths").EnumerateObject()
            .Single(property => property.Name.Equals("/orderstatuses/Histories/{orderId}", StringComparison.OrdinalIgnoreCase));
        Assert.EndsWith("/OrderHistoryStatusResponse", path.Value.GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("items").GetProperty("$ref").GetString());
        var internalProperties = schemas.GetProperty("OrderStatusHistoryResponse").GetProperty("properties");
        Assert.True(internalProperties.TryGetProperty("OrderId", out _));
        Assert.True(internalProperties.TryGetProperty("OrderStatusId", out _));
    }

    private static void AssertSourceWire(JsonElement actual, OrderStatusResponse expected)
    {
        var original = new MvcNewtonsoftJsonOptions();
        original.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        original.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var sourceBody = Newtonsoft.Json.JsonConvert.SerializeObject(expected, original.SerializerSettings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sourceBody), JsonNode.Parse(actual.GetRawText())));
    }

    private static async Task<Seed> SeedAsync(OrderStatusDbContext statuses)
    {
        var first = new OrderStatus { Name = "Earlier source", CreatedDate = new DateTime(2018, 1, 1), ModifiedDate = new DateTime(2018, 1, 2) };
        var second = new OrderStatus { Name = "In Progress", Description = "Retained description", CreatedDate = new DateTime(2019, 1, 1), ModifiedDate = new DateTime(2019, 1, 2) };
        statuses.AddRange(first, second);
        await statuses.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory
        {
            OrderId = OrderId + 1, OrderStatusId = second.Id,
            CreatedDate = new DateTime(2020, 1, 1), ModifiedDate = new DateTime(2020, 1, 2),
        });
        await statuses.SaveChangesAsync();
        var late = new OrderStatusHistory { OrderId = OrderId, OrderStatusId = first.Id, CreatedDate = new DateTime(2025, 1, 1), ModifiedDate = new DateTime(2025, 1, 2) };
        var middle = new OrderStatusHistory { OrderId = OrderId, OrderStatusId = second.Id, CreatedDate = new DateTime(2024, 1, 1), ModifiedDate = new DateTime(2024, 1, 2) };
        var early = new OrderStatusHistory { OrderId = OrderId, OrderStatusId = first.Id, CreatedDate = new DateTime(2023, 1, 1), ModifiedDate = new DateTime(2023, 1, 2) };
        statuses.History.AddRange(late, middle, early);
        await statuses.SaveChangesAsync();
        return new(first, second, early, middle, late);
    }

    private static Task<OrderStatusResponse[]> DefinitionsAsync(OrderStatusDbContext statuses) =>
        statuses.Statuses.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new OrderStatusResponse(row.Id, row.Name, row.Description, row.CreatedDate, row.ModifiedDate)).ToArrayAsync();

    private static Task<OrderStatusHistoryResponse[]> HistoryAsync(OrderStatusDbContext statuses) =>
        statuses.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new OrderStatusHistoryResponse(row.Id, row.OrderId, row.OrderStatusId,
                row.OrderStatus!.Name, row.OrderStatus.Description, row.CreatedDate, row.ModifiedDate)).ToArrayAsync();

    private sealed record Seed(OrderStatus First, OrderStatus Second, OrderStatusHistory Early, OrderStatusHistory Middle, OrderStatusHistory Late);
}
