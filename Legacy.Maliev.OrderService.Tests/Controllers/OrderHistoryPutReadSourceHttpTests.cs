using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalHistory = Maliev.OrderService.Tests.SourceHistoryCreation.OrderStatusHistory;
using OriginalStatus = Maliev.OrderService.Tests.SourceHistoryCreation.OrderStatus;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderHistoryPutReadSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 5, 6, 7, TimeSpan.Zero);
    private static int nextOwner = 97000;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PutPreservesSourceAssignmentAndIgnoredInputsAcrossBothReadProjections(bool reassign, bool nullCreated)
    {
        await using var app = await AppAsync();
        var seed = await SeedAsync(app, nullCreated);
        int destination = reassign ? seed.Destination : seed.Owner;
        var neighbors = await SnapshotAsync(app, seed.HistoryId);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var response = await PutAsync(writer, seed.HistoryId, Body(seed, destination));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.Null(response.Headers.Location);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
            var row = await db.History.AsNoTracking().SingleAsync(value => value.Id == seed.HistoryId);
            Assert.Equal(destination, row.OrderId);
            Assert.Equal(seed.TargetStatus, row.OrderStatusId);
            Assert.Equal(seed.Created, row.CreatedDate);
            Assert.Equal(Now.UtcDateTime, row.ModifiedDate);
            Assert.Equal(DateTimeKind.Unspecified, row.ModifiedDate!.Value.Kind);
        }
        Assert.Equal(neighbors, await SnapshotAsync(app, seed.HistoryId));
        var afterPut = await SnapshotAsync(app);
        using var reader = Client(app, OrderPermissions.StatusRead);
        foreach (var owner in new[] { seed.Owner, seed.Destination, seed.Foreign })
            await AssertReadsAsync(app, reader, owner);
        Assert.Equal(afterPut, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(OrderPermissions.StatusRead, HttpStatusCode.Forbidden)]
    public async Task ReassignmentRequiresExistingWriteAuthorityWithoutChangingEitherOwner(string? permission, HttpStatusCode expected)
    {
        await using var app = await AppAsync();
        var seed = await SeedAsync(app, false);
        var before = await SnapshotAsync(app);
        using var client = Client(app, permission);
        using var response = await PutAsync(client, seed.HistoryId, Body(seed, seed.Destination));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleReassignmentPreservesBothTimelinesAndEveryNeighbor(bool nullCreated)
    {
        await using var app = await AppAsync();
        var seed = await SeedAsync(app, nullCreated);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var response = await PutAsync(writer, seed.HistoryId, Body(seed, seed.Destination), "2000-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
        using var reader = Client(app, OrderPermissions.StatusRead);
        await AssertReadsAsync(app, reader, seed.Owner);
        await AssertReadsAsync(app, reader, seed.Destination);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"OrderId\":\"not-an-integer\"}")]
    public async Task InvalidBodyDoesNotReassignOrChangeStoredDates(string body)
    {
        await using var app = await AppAsync();
        var seed = await SeedAsync(app, false);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var response = await PutAsync(writer, seed.HistoryId, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task MissingRouteHistoryDoesNotUseBodyIdToModifyAnotherRow()
    {
        await using var app = await AppAsync();
        var seed = await SeedAsync(app, false);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var response = await PutAsync(writer, int.MaxValue, Body(seed, seed.Destination));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OriginalAssignmentChangesOnlyOwnerStatusAndServerModifiedDate(bool nullCreated)
    {
        var created = nullCreated ? (DateTime?)null : new DateTime(2018, 1, 2);
        var stored = new OriginalHistory { Id = 41, OrderId = 7, OrderStatusId = 11, CreatedDate = created, ModifiedDate = new DateTime(2019, 2, 3) };
        var input = Newtonsoft.Json.JsonConvert.DeserializeObject<OriginalHistory>("{\"id\":999,\"orderId\":8,\"orderStatusId\":12,\"createdDate\":\"2099-01-01\",\"modifiedDate\":\"2099-02-02\",\"orderStatus\":{\"id\":999,\"name\":\"ignored\"}}")!;
        // Exact three source PUT assignments, independent of the Legacy controller/repository.
        stored.OrderId = input.OrderId;
        stored.ModifiedDate = Now.UtcDateTime;
        stored.OrderStatusId = input.OrderStatusId;
        Assert.Equal(41, stored.Id);
        Assert.Equal(8, stored.OrderId);
        Assert.Equal(12, stored.OrderStatusId);
        Assert.Equal(created, stored.CreatedDate);
        Assert.Equal(Now.UtcDateTime, stored.ModifiedDate);
        Assert.Null(stored.OrderStatus);
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

    private static string Body(Seed seed, int destination) => JsonSerializer.Serialize(new
    {
        Id = seed.NeighborHistoryId,
        OrderId = destination,
        OrderStatusId = seed.TargetStatus,
        CreatedDate = "2099-01-01T00:00:00Z",
        ModifiedDate = "2099-02-02T00:00:00Z",
        OrderStatus = new { Id = seed.InitialStatus, Name = "ignored definition", Description = "ignored description" },
    });

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, int id, string body, string? expected = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/orderstatuses/Histories/{id}")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (expected is not null) request.Headers.Add("X-Expected-Modified-Date", expected);
        return await client.SendAsync(request);
    }

    private static async Task<Seed> SeedAsync(WebApplicationFactory<Program> app, bool nullCreated)
    {
        int owner = Interlocked.Add(ref nextOwner, 3);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var process = new Process { Name = "History test process", Category = new Category { Name = "History test category" } };
        orders.AddRange(Enumerable.Range(owner, 3).Select(id => new Order { Id = id, Name = "History test owner", Process = process, Quantity = 3, Manufactured = 1 }));
        orders.FileFormats.Add(new FileFormat { Name = "neighbor format", Extension = ".txt" });
        await orders.SaveChangesAsync();
        var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var initial = new OrderStatus { Name = "Initial source", Description = "initial definition", CreatedDate = new DateTime(2010, 1, 1), ModifiedDate = new DateTime(2011, 1, 1) };
        var target = new OrderStatus { Name = "Target source", Description = "target definition", CreatedDate = new DateTime(2012, 1, 1), ModifiedDate = new DateTime(2013, 1, 1) };
        db.AddRange(initial, target);
        await db.SaveChangesAsync();
        db.Transitions.Add(new OrderStatusTransition { OrderStatusId = initial.Id, PossibleStatusId = target.Id });
        var neighbor = new OrderStatusHistory { OrderId = owner, OrderStatusId = initial.Id, CreatedDate = new DateTime(2020, 1, 1), ModifiedDate = new DateTime(2021, 1, 1) };
        db.AddRange(neighbor,
            new OrderStatusHistory { OrderId = owner + 1, OrderStatusId = initial.Id, CreatedDate = new DateTime(2022, 1, 1), ModifiedDate = new DateTime(2023, 1, 1) },
            new OrderStatusHistory { OrderId = owner + 2, OrderStatusId = target.Id, CreatedDate = new DateTime(2024, 1, 1), ModifiedDate = new DateTime(2025, 1, 1) });
        await db.SaveChangesAsync();
        var created = nullCreated ? (DateTime?)null : new DateTime(2018, 1, 2);
        var history = new OrderStatusHistory { OrderId = owner, OrderStatusId = initial.Id, CreatedDate = created, ModifiedDate = new DateTime(2019, 1, 1) };
        db.Add(history);
        await db.SaveChangesAsync();
        // Null is a physical source case, not the generated-on-add timestamp default.
        if (nullCreated) await db.History.Where(row => row.Id == history.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.CreatedDate, (DateTime?)null));
        return new(owner, owner + 1, owner + 2, history.Id, neighbor.Id, initial.Id, target.Id, created);
    }

    private static async Task AssertReadsAsync(WebApplicationFactory<Program> app, HttpClient reader, int owner)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var storedRows = await db.History.AsNoTracking().Where(row => row.OrderId == owner).ToArrayAsync();
        // Build expected null-first source chronology independently of migrated provider ordering.
        var rows = storedRows.OrderBy(row => row.CreatedDate).ToArray();
        var definitions = await db.Statuses.AsNoTracking().ToDictionaryAsync(row => row.Id);
        Assert.NotEmpty(rows);
        var timeline = rows.Select((row, index) => new OriginalStatus
        {
            Id = index + 1, Name = definitions[row.OrderStatusId].Name, Description = definitions[row.OrderStatusId].Description,
            CreatedDate = row.CreatedDate, ModifiedDate = row.ModifiedDate,
            OrderStatusHasPossibleStatusOrderStatus = null!, OrderStatusHasPossibleStatusPossibleStatus = null!, OrderStatusHistory = null!,
        }).ToArray();
        using var historyResponse = await reader.GetAsync($"/orderstatuses/Histories/{owner}");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var historyBody = await historyResponse.Content.ReadAsStringAsync();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(timeline)), JsonNode.Parse(historyBody)));
        if (storedRows.Any(row => row.CreatedDate is null))
        {
            using var timelineDocument = JsonDocument.Parse(historyBody);
            Assert.False(timelineDocument.RootElement[0].TryGetProperty("createdDate", out _));
            Assert.Equal(1, timelineDocument.RootElement[0].GetProperty("id").GetInt32());
        }
        var consumer = JsonSerializer.Deserialize<OrderStatusHistoryResponse[]>(historyBody, JsonSerializerOptions.Web)!;
        Assert.Equal(timeline.Select(row => row.Name), consumer.Select(row => row.Name));
        Assert.Equal(timeline.Select(row => row.CreatedDate), consumer.Select(row => row.CreatedDate));
        var latest = rows.MaxBy(row => row.Id)!;
        var definition = definitions[latest.OrderStatusId];
        var original = new OriginalStatus { Id = definition.Id, Name = definition.Name, Description = definition.Description, CreatedDate = definition.CreatedDate, ModifiedDate = definition.ModifiedDate };
        original.OrderStatusHistory.Add(new OriginalHistory { Id = latest.Id, OrderId = latest.OrderId, OrderStatusId = latest.OrderStatusId, CreatedDate = latest.CreatedDate, ModifiedDate = latest.ModifiedDate, OrderStatus = original });
        using var latestResponse = await reader.GetAsync($"/orderstatuses/Histories/{owner}/latest");
        Assert.Equal(HttpStatusCode.OK, latestResponse.StatusCode);
        var latestBody = await latestResponse.Content.ReadAsStringAsync();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(original)), JsonNode.Parse(latestBody)));
    }

    private static string SerializeOriginal(object value)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, options.SerializerSettings);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int excludedHistoryId = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Deletions = await orders.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().Where(row => row.Id != excludedHistoryId).OrderBy(row => row.Id).ToArrayAsync(),
        });
    }

    private sealed record Seed(int Owner, int Destination, int Foreign, int HistoryId, int NeighborHistoryId, int InitialStatus, int TargetStatus, DateTime? Created);
}
