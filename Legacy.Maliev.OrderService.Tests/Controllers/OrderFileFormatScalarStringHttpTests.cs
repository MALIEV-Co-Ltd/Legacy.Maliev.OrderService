using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileFormatScalarStringHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false, "123", "123")]
    [InlineData(true, "123", "123")]
    [InlineData(false, "-0", "-0")]
    [InlineData(true, "-0", "-0")]
    [InlineData(false, "12.3400E+03", "12.3400E+03")]
    [InlineData(true, "12.3400E+03", "12.3400E+03")]
    [InlineData(false, "true", "true")]
    [InlineData(true, "true", "true")]
    [InlineData(false, "false", "false")]
    [InlineData(true, "false", "false")]
    [InlineData(false, "\"  ไทย .STEP  \"", "  ไทย .STEP  ")]
    [InlineData(true, "\"  ไทย .STEP  \"", "  ไทย .STEP  ")]
    [InlineData(false, "\"\"", "")]
    [InlineData(true, "\"\"", "")]
    [InlineData(false, "null", null)]
    [InlineData(true, "null", null)]
    public async Task PostAndPut_SourcePrimitiveText_RemainsLiteralNullableString(bool update, string token, string? expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app, original?.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await SendAsync(writer, update, original?.Id ?? 0, Payload(token));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = original?.Id ?? (await response.Content.ReadFromJsonAsync<FileFormatResponse>())!.Id;
        if (!update)
        {
            Assert.NotNull(response.Headers.Location);
            using var location = await reader.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, location.StatusCode);
            Assert.Equal(id, (await location.Content.ReadFromJsonAsync<FileFormatResponse>())!.Id);
        }
        using var fresh = await reader.GetAsync($"/orders/fileformats/{id}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        foreach (var field in new[] { "Name", "Extension" })
        {
            if (expected is null) Assert.False(wire.RootElement.TryGetProperty(field, out _));
            else
            {
                Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty(field).ValueKind);
                Assert.Equal(expected, wire.RootElement.GetProperty(field).GetString());
            }
        }
        var current = (await reader.GetFromJsonAsync<FileFormatResponse>($"/orders/fileformats/{id}"))!;
        Assert.Equal(expected, current.Name);
        Assert.Equal(expected, current.Extension);
        var list = (await reader.GetFromJsonAsync<FileFormatResponse[]>("/orders/fileformats"))!;
        Assert.Equal(2, list.Length);
        Assert.Contains(current, list);
        if (original is not null)
        {
            Assert.Equal(original.CreatedDate, current.CreatedDate);
            Assert.True(current.ModifiedDate > original.ModifiedDate);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var row = await database.FileFormats.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(expected, row.Name);
        Assert.Equal(expected, row.Extension);
        Assert.Equal(current.CreatedDate, row.CreatedDate);
        Assert.Equal(current.ModifiedDate, row.ModifiedDate);
        Assert.Equal(2, await database.FileFormats.CountAsync());
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    public async Task NestedFileFormatText_IsBadRequestWithoutGraphChanges(bool update, string token)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(writer, update, original?.Id ?? 0, Payload(token));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScalarFileFormatWrite_AnonymousOrReadOnlyGrant_CannotChangeGraph(bool update, bool anonymous)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var owner = Client(app, OrderPermissions.CatalogWrite);
        var original = update ? await CreateControlAsync(owner) : null;
        using var client = anonymous ? app.CreateClient() : Client(app, OrderPermissions.CatalogRead);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(client, update, original?.Id ?? 0, Payload("123"));
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    public async Task MissingFileFormat_SourceScalarBinding_ReachesNotFoundWithoutWrites(string token)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(writer, true, 999999, Payload(token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public void Converter_IsFileFormatRequestOnly_AndSerializationStillWritesStrings()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"Bucket\":123,\"ObjectName\":\"metadata-only\"}"));
        var request = JsonSerializer.Deserialize<UpsertFileFormatRequest>(Payload("12.3400E+03"))!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request));
        foreach (var field in new[] { "Name", "Extension" })
        {
            Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty(field).ValueKind);
            Assert.Equal("12.3400E+03", wire.RootElement.GetProperty(field).GetString());
        }
    }

    private Task<WebApplicationFactory<Program>> AppAsync(FakeTimeProvider clock) => fixture.AppAsync(null, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
    });

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Payload(string token) => $"{{\"Name\":{token},\"Extension\":{token}}}";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, int id, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return update ? await client.PutAsync($"/orders/fileformats/{id}", content) : await client.PostAsync("/orders/fileformats", content);
    }

    private static async Task<FileFormatResponse> CreateControlAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/orders/fileformats", new UpsertFileFormatRequest("Control", ".old"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<FileFormatResponse>())!;
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        orders.Categories.Add(new Category { Id = 9001, Name = "Unrelated category" });
        orders.Processes.Add(new Process { Id = 9002, CategoryId = 9001, Name = "Unrelated process" });
        orders.Orders.Add(new Order { Id = 9003, ProcessId = 9002, Name = "Unrelated order", Quantity = 1 });
        orders.FileFormats.Add(new FileFormat { Id = 9004, Name = "Unrelated format", Extension = ".sentinel" });
        orders.Files.Add(new OrderFile { Id = 9005, OrderId = 9003, Bucket = "synthetic", ObjectName = "metadata-only" });
        await orders.SaveChangesAsync();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        statuses.Statuses.AddRange(new OrderStatus { Id = 9006, Name = "Unrelated status" }, new OrderStatus { Id = 9007, Name = "Unrelated next status" });
        statuses.Transitions.Add(new OrderStatusTransition { Id = 9008, OrderStatusId = 9006, PossibleStatusId = 9007 });
        statuses.History.Add(new OrderStatusHistory { Id = 9009, OrderId = 9003, OrderStatusId = 9006 });
        await statuses.SaveChangesAsync();
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int? excludedFormatId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().Where(value => excludedFormatId == null || value.Id != excludedFormatId).OrderBy(value => value.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
        });
    }
}
