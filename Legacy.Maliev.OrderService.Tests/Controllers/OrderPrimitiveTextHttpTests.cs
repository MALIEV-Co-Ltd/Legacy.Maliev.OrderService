using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderPrimitiveTextHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    // Golden values come from the retained Newtonsoft 13.0.3 ReadAsString source,
    // not from the candidate converter. This is not original-binary execution.
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
    [InlineData(false, "\"\"", "")]
    [InlineData(true, "\"\"", "")]
    [InlineData(false, "\"  ไทย 😀  \"", "  ไทย 😀  ")]
    [InlineData(true, "\"  ไทย 😀  \"", "  ไทย 😀  ")]
    public async Task PostAndPut_PrimitiveText_PreservesGoldenLiteralAndFreshStoredValues(
        bool update, string token, string expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var reader = Client(app, OrderPermissions.Read);
        var before = await SnapshotAsync(app, update ? 9003 : null);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await SendAsync(writer, update, 9003, Payload(token, token));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        if (!update)
        {
            Assert.NotNull(response.Headers.Location);
            using var location = await reader.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, location.StatusCode);
            Assert.Equal(id, (await location.Content.ReadFromJsonAsync<OrderResponse>())!.Id);
        }
        using var fresh = await reader.GetAsync($"/Orders/{id}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        foreach (var field in new[] { "Name", "Description" })
        {
            Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty(field).ValueKind);
            Assert.Equal(expected, wire.RootElement.GetProperty(field).GetString());
        }
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var stored = await database.Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(expected, stored.Name);
        Assert.Equal(expected, stored.Description);
        Assert.Equal(9002, stored.ProcessId);
        Assert.Equal(1, stored.Quantity);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, stored.ModifiedDate);
        Assert.Equal(update ? InitialTimestamp : clock.GetUtcNow().UtcDateTime, stored.CreatedDate);
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false, "Name", "{}")]
    [InlineData(true, "Name", "{}")]
    [InlineData(false, "Name", "[]")]
    [InlineData(true, "Name", "[]")]
    [InlineData(false, "Description", "{}")]
    [InlineData(true, "Description", "{}")]
    [InlineData(false, "Description", "[]")]
    [InlineData(true, "Description", "[]")]
    public async Task NestedText_RemainsBadRequestWithoutAnyStoredChanges(bool update, string field, string token)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(writer, update, 9003,
            Payload(field == "Name" ? token : "\"Control\"", field == "Description" ? token : "\"Control\""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, 100, 250, true)]
    [InlineData(true, 100, 250, true)]
    [InlineData(false, 101, 250, false)]
    [InlineData(true, 101, 250, false)]
    [InlineData(false, 100, 251, false)]
    [InlineData(true, 100, 251, false)]
    public async Task StringLengthLimits_AreStillEnforcedBeforePersistence(
        bool update, int nameLength, int descriptionLength, bool accepted)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        var before = await SnapshotAsync(app, accepted && update ? 9003 : null);
        var name = new string('ก', nameLength);
        var description = new string('ข', descriptionLength);
        using var response = await SendAsync(writer, update, 9003,
            Payload(JsonSerializer.Serialize(name), JsonSerializer.Serialize(description)));
        Assert.Equal(accepted ? update ? HttpStatusCode.NoContent : HttpStatusCode.Created : HttpStatusCode.BadRequest,
            response.StatusCode);
        if (!accepted)
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(nameLength > 100 ? "Name" : "Description", out _));
            Assert.Equal(before, await SnapshotAsync(app));
            return;
        }
        var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(name, stored.Name);
        Assert.Equal(description, stored.Description);
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(false, "Name", 101)]
    [InlineData(true, "Name", 101)]
    [InlineData(false, "Description", 251)]
    [InlineData(true, "Description", 251)]
    public async Task ConvertedNumericText_StillReceivesExistingLengthValidation(bool update, string field, int length)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        var before = await SnapshotAsync(app);
        var number = new string('1', length);
        using var response = await SendAsync(writer, update, 9003,
            Payload(field == "Name" ? number : "\"Control\"", field == "Description" ? number : "\"Control\""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, "Name", 51)]
    [InlineData(true, "Name", 51)]
    [InlineData(false, "Description", 126)]
    [InlineData(true, "Description", 126)]
    [InlineData(false, "Name", 50)]
    [InlineData(true, "Name", 50)]
    [InlineData(false, "Description", 125)]
    [InlineData(true, "Description", 125)]
    public async Task SupplementaryText_LimitsRemainUtf16NotUnicodeScalarCount(bool update, string field, int count)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        var before = await SnapshotAsync(app);
        var literal = string.Concat(Enumerable.Repeat("😀", count));
        var token = JsonSerializer.Serialize(literal);
        var accepted = literal.Length <= (field == "Name" ? 100 : 250);
        using var response = await SendAsync(writer, update, 9003,
            Payload(field == "Name" ? token : "\"Control\"", field == "Description" ? token : "\"Control\""));
        Assert.Equal(accepted ? update ? HttpStatusCode.NoContent : HttpStatusCode.Created : HttpStatusCode.BadRequest,
            response.StatusCode);
        if (!accepted) Assert.Equal(before, await SnapshotAsync(app));
        else
        {
            var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
            await using var scope = app.Services.CreateAsyncScope();
            var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
            Assert.Equal(literal, field == "Name" ? stored.Name : stored.Description);
        }
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task NullOrOmittedName_PreservesCurrentCreateDefaultAndNullableUpdate(bool update, string? name)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var reader = Client(app, OrderPermissions.Read);
        var payload = name is null ? "{\"ProcessId\":9002,\"Quantity\":1}" : Payload(name, null);
        using var response = await SendAsync(writer, update, 9003, payload);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        using var read = await reader.GetAsync($"/Orders/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        if (update)
        {
            Assert.Null(stored.Name);
            Assert.False(wire.RootElement.TryGetProperty("Name", out _));
        }
        else
        {
            Assert.Equal("unnamed", stored.Name);
            Assert.Equal("unnamed", wire.RootElement.GetProperty("Name").GetString());
        }
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task NullOrOmittedDescription_PreservesCurrentConsumerShape(bool update, string? description)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var reader = Client(app, OrderPermissions.Read);
        using var response = await SendAsync(writer, update, 9003, Payload("\"Control\"", description));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        using var read = await reader.GetAsync($"/Orders/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var wire = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("Control", wire.RootElement.GetProperty("Name").GetString());
        Assert.False(wire.RootElement.TryGetProperty("Description", out _));
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id)).Description);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PrimitiveText_AnonymousOrReadOnlyToken_DoesNotGrantWrite(bool update, bool anonymous)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var client = anonymous ? app.CreateClient() : Client(app, OrderPermissions.Read);
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(client, update, 9003, Payload("123", "true"));
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrimitiveText_UpdatePreservesNotFoundAndConcurrencyRefusals(bool missing)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.Update);
        if (!missing) writer.DefaultRequestHeaders.Add("X-Expected-Modified-Date", "2000-01-01T00:00:00Z");
        var before = await SnapshotAsync(app);
        using var response = await SendAsync(writer, true, missing ? 999999 : 9003, Payload("123", "true"));
        Assert.Equal(missing ? HttpStatusCode.NotFound : HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public void RequestOnlyBinding_WritesStringsAndPreservesNullAndOmission()
    {
        var request = JsonSerializer.Deserialize<UpsertOrderRequest>(Payload("12.3400E+03", "true"))!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.Equal("12.3400E+03", wire.RootElement.GetProperty("Name").GetString());
        Assert.Equal("true", wire.RootElement.GetProperty("Description").GetString());
        foreach (var payload in new[] { "{}", "{\"Name\":null,\"Description\":null}" })
        {
            var empty = JsonSerializer.Deserialize<UpsertOrderRequest>(payload)!;
            Assert.Null(empty.Name);
            Assert.Null(empty.Description);
        }
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OrderResponse>("{\"Name\":123}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderRequest>("{\"Comment\":123}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderRequest>("{\"TrackingNumber\":true}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderRequest>("{\"OperationKey\":123}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedConsumerJsonContent_PreservesLiteralStringsAndCamelCaseInput(bool update)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        if (!update) writer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var request = JsonSerializer.Deserialize<UpsertOrderRequest>(Payload("\"  ไทย 😀  \"", "\"  requirements  \""))!;
        // The retained consumer uses JsonContent.Create with web defaults.
        using var content = JsonContent.Create(request);
        using (var sent = JsonDocument.Parse(await content.ReadAsStringAsync()))
        {
            Assert.Equal("  ไทย 😀  ", sent.RootElement.GetProperty("name").GetString());
            Assert.Equal("  requirements  ", sent.RootElement.GetProperty("description").GetString());
        }
        using var response = update ? await writer.PutAsync("/Orders/9003", content) : await writer.PostAsync("/Orders", content);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal(request.Name, stored.Name);
        Assert.Equal(request.Description, stored.Description);
    }

    private static DateTime InitialTimestamp => new(2026, 10, 16, 0, 0, 0, DateTimeKind.Unspecified);

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

    private static string Payload(string name, string? description) =>
        $"{{\"ProcessId\":9002,\"Quantity\":1,\"Name\":{name}" +
        (description is null ? "}" : $",\"Description\":{description}}}");

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, int id, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return update ? await client.PutAsync($"/Orders/{id}", content) : await client.PostAsync("/Orders", content);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        database.Categories.Add(new Category { Id = 9001, Name = "Unrelated category" });
        database.Processes.Add(new Process { Id = 9002, CategoryId = 9001, Name = "Unrelated process" });
        database.Orders.AddRange(
            new Order { Id = 9003, ProcessId = 9002, Name = "Original", Description = "Original description", Quantity = 1, CreatedDate = InitialTimestamp, ModifiedDate = InitialTimestamp },
            new Order { Id = 9004, ProcessId = 9002, Name = "Unrelated", Quantity = 1 });
        await database.SaveChangesAsync();
        // The existing fixture shares Redis across fresh per-case databases.
        foreach (var id in new[] { 1, 9003, 9004 })
            await scope.ServiceProvider.GetRequiredService<IOrderCache>().RemoveAsync($"order:{id}", CancellationToken.None);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int? excludedOrderId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await database.Orders.AsNoTracking().Where(value => excludedOrderId == null || value.Id != excludedOrderId)
                .OrderBy(value => value.Id).Select(value => new { value.Id, value.Name, value.Description, value.ProcessId, value.Quantity, value.CreatedDate, value.ModifiedDate }).ToArrayAsync(),
            Categories = await database.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await database.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Files = await database.Files.CountAsync(),
            DeletionIntents = await database.DeletionIntents.CountAsync(),
            Statuses = await statuses.Statuses.CountAsync(),
            Transitions = await statuses.Transitions.CountAsync(),
            History = await statuses.History.CountAsync(),
        });
    }
}
