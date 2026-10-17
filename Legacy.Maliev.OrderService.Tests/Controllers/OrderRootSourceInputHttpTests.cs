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

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderRootSourceInputHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    public static TheoryData<bool, string, string?> Keys => new()
    {
        { false, "\"2026-10-16T00:00:00Z\"", "2026-10-16T00:00:00Z" },
        { true, "\"2026-10-16T00:00:00Z\"", "2026-10-16T00:00:00Z" },
        { false, "\"ไทย\\uD83D\\uDE00\"", "ไทย😀" },
        { true, "\"ไทย\\uD83D\\uDE00\"", "ไทย😀" },
        { false, "\"�\"", "�" }, { true, "\"�\"", "�" },
        { false, "\"a\\\"b\\\\c\"", "a\"b\\c" },
        { true, "\"a\\\"b\\\\c\"", "a\"b\\c" },
        { false, "null", null }, { true, "null", null },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task FullSourceGraphAndStrictKey_UseRealRootPostPutAndScalarPersistence(
        bool update, string token, string? expected)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app, update ? 9003 : null);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var response = await SendAsync(writer, update, LegacyPayload(token));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        int id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var stored = await db.Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal("123", stored.Name);
        Assert.Equal("true", stored.Description);
        Assert.Equal(9002, stored.ProcessId);
        Assert.Equal(1, stored.Quantity);
        Assert.Null(stored.CustomerId);
        Assert.Equal("legacy", stored.Comment);
        Assert.Equal("42", stored.TrackingNumber);
        // Existing durable keys are create-only; PUT must not assign a new key.
        Assert.Equal(update ? null : expected, stored.OperationKey);
        Assert.NotEqual(777, stored.Id);
        Assert.NotEqual(777m, stored.Subtotal);
        Assert.Equal(before, await SnapshotAsync(app, id));
        using var reader = Client(app, OrderPermissions.Read);
        using var read = await reader.GetAsync("/Orders/" + id);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("123", (await read.Content.ReadFromJsonAsync<OrderResponse>())!.Name);
    }

    public static TheoryData<bool, string> RejectedKeys => new()
    {
        { false, "123" }, { true, "123" }, { false, "true" }, { true, "true" },
        { false, "false" }, { true, "false" }, { false, "{}" }, { true, "{}" },
        { false, "[]" }, { true, "[]" }, { false, "undefined" }, { true, "undefined" },
        { false, "'single'" }, { true, "'single'" },
        { false, "\"\\uD800\"" }, { true, "\"\\uD800\"" },
        { false, "\"\\uDC00\"" }, { true, "\"\\uDC00\"" },
    };

    [Theory]
    [MemberData(nameof(RejectedKeys))]
    public async Task InvalidKey_NeverReachesPersistenceOrEchoesRawToken(bool update, string token)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var response = await SendAsync(writer, update, LegacyPayload(token));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = wire.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("OperationKey", out var keyErrors));
        Assert.Equal("The operation key must be a valid JSON string or null.", Assert.Single(keyErrors.EnumerateArray()).GetString());
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, 100, 250, 128, true)]
    [InlineData(true, 100, 250, 128, true)]
    [InlineData(false, 101, 250, 128, false)]
    [InlineData(true, 101, 250, 128, false)]
    [InlineData(false, 100, 251, 128, false)]
    [InlineData(true, 100, 251, 128, false)]
    [InlineData(false, 100, 250, 129, false)]
    [InlineData(true, 100, 250, 129, false)]
    public async Task ExistingRecordValidation_UsesUtf16LengthsAfterSourceProjection(
        bool update, int nameLength, int descriptionLength, int keyLength, bool accepted)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app, accepted && update ? 9003 : null);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        string key = new string('ก', keyLength - 2) + "😀";
        string payload = "{ProcessId:9002,Quantity:1,Name:" + JsonSerializer.Serialize(new string('น', nameLength)) +
            ",Description:" + JsonSerializer.Serialize(new string('ร', descriptionLength)) +
            ",OperationKey:" + JsonSerializer.Serialize(key) + "}";
        using var response = await SendAsync(writer, update, payload);
        Assert.Equal(accepted ? update ? HttpStatusCode.NoContent : HttpStatusCode.Created : HttpStatusCode.BadRequest, response.StatusCode);
        int? id = accepted ? update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id : null;
        Assert.Equal(before, await SnapshotAsync(app, id));
        if (!accepted)
        {
            using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            string field = nameLength > 100 ? "Name" : descriptionLength > 250 ? "Description" : "OperationKey";
            Assert.True(wire.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateLastNullAndStockUtf16Charset_ArePreserved(bool update)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        string payload = "{\r\nProcessId:9002,Quantity:1,Name:'kept',Name:null," +
            "\"oper\\u0061tionKEY\":\"first\",OperationKey:/* kept */null}";
        using var content = new StringContent(payload, Encoding.Unicode, "application/json");
        using var response = update ? await writer.PutAsync("/Orders/9003", content) : await writer.PostAsync("/Orders", content);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        int id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal("kept", stored.Name);
        Assert.Null(stored.OperationKey);
    }

    [Fact]
    public async Task HeaderFingerprint_UsesDecodedKeyAndRetainsReplayAndConflict()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.Create);
        writer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var first = await SendAsync(writer, false, LegacyPayload("\"same😀\""));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        int id = (await first.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        using var replay = await SendAsync(writer, false, LegacyPayload("\"same\\uD83D\\uDE00\""));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<OrderResponse>())!.Id);
        using var conflict = await SendAsync(writer, false, LegacyPayload("\"different\""));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        // Without a header reservation, existing durable OperationKey replay is unchanged.
        writer.DefaultRequestHeaders.Remove("Idempotency-Key");
        using var durable = await SendAsync(writer, false, LegacyPayload("\"same😀\""));
        Assert.Equal(HttpStatusCode.Created, durable.StatusCode);
        Assert.Equal(id, (await durable.Content.ReadFromJsonAsync<OrderResponse>())!.Id);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.CountAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingAuthenticationAndPermissionChecks_StillPrecedeMutation(bool update, bool authenticated)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = authenticated ? Client(app, OrderPermissions.Read) : app.CreateClient();
        using var response = await SendAsync(client, update, LegacyPayload("\"owned\""));
        Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StockHttpBomBehavior_IsNotSilentlyNormalized(bool update, bool unicode)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        var encoding = unicode ? Encoding.Unicode : Encoding.UTF8;
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(LegacyPayload("\"ไทย😀\""))).ToArray();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = unicode ? "utf-16" : "utf-8" };
        using var response = update ? await writer.PutAsync("/Orders/9003", content) : await writer.PostAsync("/Orders", content);
        // MVC's actual HttpRequestStreamReader decodes but does not strip BOM.
        // Stock Json.NET rejects that leading U+FEFF; do not normalize/replay it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task UnsupportedRootCharset_StillReturns415WithoutMutation()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.Create);
        using var content = new StringContent(LegacyPayload("\"kept\""), Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "iso-8859-1";
        using var response = await writer.PostAsync("/Orders", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RootReferenceCycles_PostPutSucceedWithoutNavigationPersistence(bool update, bool fileCycle)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app, update ? 9003 : null);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        string graph = fileCycle
            ? "OrderFile:[{Id:81234,Order:{$ref:'root'}},{Order:{Name:'nested',OperationKey:123}}]"
            : "Process:{Id:81234,Category:{Id:81235},Order:[{$ref:'root'},{Name:'nested',OperationKey:123}]}";
        string body = "{$id:'root',ProcessId:9002,Quantity:1,Name:'cycle',OperationKey:\"cycle-key\"," + graph + "}";
        using var response = await SendAsync(writer, update, body);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        int id = update ? 9003 : (await response.Content.ReadFromJsonAsync<OrderResponse>())!.Id;
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var stored = await db.Orders.AsNoTracking().SingleAsync(value => value.Id == id);
        Assert.Equal("cycle", stored.Name);
        Assert.Equal(9002, stored.ProcessId);
        Assert.Equal(update ? null : "cycle-key", stored.OperationKey);
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    private static string LegacyPayload(string key) =>
        "{Id:777,ProcessId:9002,ProcessId:null,Quantity:1,Quantity:null,Name:123,Name:null," +
        "Description:true,CustomerId:null,Comment:'legacy',TrackingNumber:42,Remaining:777,Subtotal:777," +
        "CreatedDate:'2000-01-01T00:00:00',ModifiedDate:'2000-01-01T00:00:00'," +
        "Process:{Name:'inert',Category:{Name:'inert'},Order:[{Name:'inert',OrderFile:[{Order:{Quantity:2}}]}]}," +
        "OrderFile:[{OrderId:9003,Bucket:'inert',ObjectName:'inert',Order:{Name:'inert'}}]," +
        "Unknown:{OperationKey:'not a root key'},OperationKey:" + key + "}";

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return update ? await client.PutAsync("/Orders/9003", content) : await client.PostAsync("/Orders", content);
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.Categories.Add(new Category { Id = 9001, Name = "Unrelated category" });
        db.Processes.Add(new Process { Id = 9002, CategoryId = 9001, Name = "Unrelated process" });
        db.Orders.AddRange(new Order { Id = 9003, ProcessId = 9002, Name = "Original", Quantity = 1 },
            new Order { Id = 9004, ProcessId = 9002, Name = "Unrelated", Quantity = 1 });
        await db.SaveChangesAsync();
        foreach (int id in new[] { 1, 9003, 9004 })
            await scope.ServiceProvider.GetRequiredService<IOrderCache>().RemoveAsync("order:" + id, CancellationToken.None);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, int? excludedId = null)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await db.Orders.AsNoTracking().Where(value => excludedId == null || value.Id != excludedId)
                .OrderBy(value => value.Id).ToArrayAsync(),
            Categories = await db.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await db.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Files = await db.Files.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            DeletionIntents = await db.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
        });
    }
}
