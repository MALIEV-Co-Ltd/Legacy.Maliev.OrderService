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
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderStatusScalarStringHttpTests(OrderDeletionReadinessFixture fixture)
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
    public async Task PostAndPut_SourcePrimitiveText_RemainsLiteralNullableString(bool update, string token, string? expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var reader = Client(app, OrderPermissions.StatusRead);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app, original?.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await SendAsync(writer, update, original?.Id ?? 0, Payload(token));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = original?.Id ?? (await response.Content.ReadFromJsonAsync<OrderStatusResponse>())!.Id;
        if (!update)
        {
            Assert.NotNull(response.Headers.Location);
            using var location = await reader.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, location.StatusCode);
            Assert.Equal(id, (await location.Content.ReadFromJsonAsync<OrderStatusResponse>())!.Id);
        }
        using var fresh = await reader.GetAsync($"/orderstatuses/{id}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        foreach (var field in new[] { "Name", "Description" })
        {
            if (expected is null) Assert.False(wire.RootElement.TryGetProperty(field, out _));
            else
            {
                Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty(field).ValueKind);
                Assert.Equal(expected, wire.RootElement.GetProperty(field).GetString());
            }
        }
        var current = (await reader.GetFromJsonAsync<OrderStatusResponse>($"/orderstatuses/{id}"))!;
        Assert.Equal(expected, current.Name);
        Assert.Equal(expected, current.Description);
        var list = (await reader.GetFromJsonAsync<OrderStatusResponse[]>("/orderstatuses"))!;
        Assert.Equal(3, list.Length);
        Assert.Contains(current, list);
        if (original is not null)
        {
            Assert.Equal(original.CreatedDate, current.CreatedDate);
            Assert.True(current.ModifiedDate > original.ModifiedDate);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var row = await database.Statuses.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(expected, row.Name);
        Assert.Equal(expected, row.Description);
        Assert.Equal(current.CreatedDate, row.CreatedDate);
        Assert.Equal(current.ModifiedDate, row.ModifiedDate);
        Assert.Equal(3, await database.Statuses.CountAsync());
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    public async Task NestedOrderStatusText_IsBadRequestWithoutGraphChanges(bool update, string token)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
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
    public async Task ScalarOrderStatusWrite_AnonymousOrReadOnlyGrant_CannotChangeGraph(bool update, bool anonymous)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var owner = Client(app, OrderPermissions.StatusWrite);
        var original = update ? await CreateControlAsync(owner) : null;
        using var client = anonymous ? app.CreateClient() : Client(app, OrderPermissions.StatusRead);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(client, update, original?.Id ?? 0, Payload("123"));
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    public async Task MissingOrderStatus_SourceScalarBinding_ReachesNotFoundWithoutWrites(string token)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(writer, true, 999999, Payload(token));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NullableDescription_OmittedOrExplicitNull_PreservesRequiredScalarName(bool update, bool omitted)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var reader = Client(app, OrderPermissions.StatusRead);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app, original?.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        var payload = omitted ? "{\"Name\":true}" : "{\"Name\":true,\"Description\":null}";
        using var response = await SendAsync(writer, update, original?.Id ?? 0, payload);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = original?.Id ?? (await response.Content.ReadFromJsonAsync<OrderStatusResponse>())!.Id;
        var current = (await reader.GetFromJsonAsync<OrderStatusResponse>($"/orderstatuses/{id}"))!;
        Assert.Equal("true", current.Name);
        Assert.Null(current.Description);
        using var named = await reader.GetAsync("/orderstatuses/true");
        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        Assert.Equal(id, (await named.Content.ReadFromJsonAsync<OrderStatusResponse>())!.Id);
        using var fresh = await reader.GetAsync($"/orderstatuses/{id}");
        using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("Name").ValueKind);
        Assert.False(wire.RootElement.TryGetProperty("Description", out _));
        if (original is not null)
        {
            Assert.Equal(original.CreatedDate, current.CreatedDate);
            Assert.True(current.ModifiedDate > original.ModifiedDate);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var row = await statuses.Statuses.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal("true", row.Name);
        Assert.Null(row.Description);
        Assert.Equal(current.CreatedDate, row.CreatedDate);
        Assert.Equal(current.ModifiedDate, row.ModifiedDate);
        Assert.Equal(3, await statuses.Statuses.CountAsync());
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false, "null-name")]
    [InlineData(true, "null-name")]
    [InlineData(false, "long-name")]
    [InlineData(true, "long-name")]
    [InlineData(false, "long-description")]
    [InlineData(true, "long-description")]
    public async Task ExistingRequiredAndLengthConstraints_StayBadRequestWithoutWrites(bool update, string invalid)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app);
        var payload = invalid switch
        {
            "null-name" => "{\"Name\":null,\"Description\":true}",
            "long-name" => JsonSerializer.Serialize(new { Name = new string('N', 51), Description = "unchanged constraint" }),
            _ => JsonSerializer.Serialize(new { Name = "unchanged constraint", Description = new string('D', 101) }),
        };
        using var response = await SendAsync(writer, update, original?.Id ?? 0, payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task ThaiSupplementaryString_LengthUsesExistingUtf16Boundary(bool update, bool description, bool overflow)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var reader = Client(app, OrderPermissions.StatusRead);
        var original = update ? await CreateControlAsync(writer) : null;
        var before = await SnapshotAsync(app, overflow ? null : original?.Id);
        var maximum = description ? 100 : 50;
        var pairs = (maximum - 4) / 2 + (overflow ? 1 : 0);
        var text = "ไทยX" + string.Concat(Enumerable.Repeat("😀", pairs));
        Assert.Equal(maximum + (overflow ? 2 : 0), text.Length);
        var expectedName = description ? "Reviewed" : text;
        var expectedDescription = description ? text : "Literal Thai and supplementary text";
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await SendAsync(writer, update, original?.Id ?? 0, JsonSerializer.Serialize(new { Name = expectedName, Description = expectedDescription }));
        Assert.Equal(overflow ? HttpStatusCode.BadRequest : update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        if (overflow)
        {
            Assert.Equal(before, await SnapshotAsync(app));
            return;
        }
        var id = original?.Id ?? (await response.Content.ReadFromJsonAsync<OrderStatusResponse>())!.Id;
        var current = (await reader.GetFromJsonAsync<OrderStatusResponse>($"/orderstatuses/{id}"))!;
        Assert.Equal(expectedName, current.Name);
        Assert.Equal(expectedDescription, current.Description);
        if (original is not null)
        {
            Assert.Equal(original.CreatedDate, current.CreatedDate);
            Assert.True(current.ModifiedDate > original.ModifiedDate);
        }
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var row = await statuses.Statuses.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(expectedName, row.Name);
        Assert.Equal(expectedDescription, row.Description);
        Assert.Equal(current.CreatedDate, row.CreatedDate);
        Assert.Equal(current.ModifiedDate, row.ModifiedDate);
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Fact]
    public void Converter_IsOrderStatusRequestOnly_AndSerializationStillWritesStrings()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"OrderId\":{},\"Bucket\":\"metadata-only\",\"ObjectName\":\"metadata-only\"}"));
        var request = JsonSerializer.Deserialize<UpsertOrderStatusRequest>(Payload("12.3400E+03"))!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request));
        foreach (var field in new[] { "Name", "Description" })
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

    private static string Payload(string token) => $"{{\"Name\":{token},\"Description\":{token}}}";

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, int id, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return update ? await client.PutAsync($"/orderstatuses/{id}", content) : await client.PostAsync("/orderstatuses", content);
    }

    private static async Task<OrderStatusResponse> CreateControlAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/orderstatuses", new UpsertOrderStatusRequest("Control", ".old"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderStatusResponse>())!;
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
        await scope.ServiceProvider.GetRequiredService<IDistributedCache>().SetStringAsync("status-scalar:sentinel", "unchanged", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int? excludedStatusId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            CacheSentinel = await scope.ServiceProvider.GetRequiredService<IDistributedCache>().GetStringAsync("status-scalar:sentinel"),
            Orders = await orders.Orders.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().Where(value => excludedStatusId == null || value.Id != excludedStatusId).OrderBy(value => value.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
        });
    }
}
