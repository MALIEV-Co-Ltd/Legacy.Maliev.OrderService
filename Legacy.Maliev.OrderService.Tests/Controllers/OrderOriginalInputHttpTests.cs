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
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderOriginalInputHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    public static TheoryData<string, bool> Actions => new()
    {
        { "category", false }, { "category", true }, { "format", false }, { "format", true },
        { "process", false }, { "process", true }, { "status", false }, { "status", true },
        { "file", true }, { "history", true },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task OrderedNullsAndInertGraphs_ProjectOnlySourceScalars(string kind, bool update)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app, kind, update ? Id(kind) : -1);
        using var writer = Client(app, WritePermission(kind));
        using var response = await SendAsync(writer, kind, update, Payload(kind));
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? Id(kind) : await CreatedIdAsync(response, kind);
        await AssertStoredAsync(app, kind, id);
        Assert.Equal(before, await SnapshotAsync(app, kind, id));
        if (!update)
        {
            Assert.NotNull(response.Headers.Location);
            using var reader = Client(app, ReadPermission(kind));
            using var fresh = await reader.GetAsync(response.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
            using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
            Assert.Equal(id, wire.RootElement.GetProperty(kind is "format" or "status" or "process" ? "id" : "Id").GetInt32());
            if (kind == "process")
            {
                Assert.Empty(wire.RootElement.GetProperty("order").EnumerateArray());
                Assert.False(wire.RootElement.TryGetProperty("category", out _));
            }
            Assert.False(wire.RootElement.TryGetProperty("Process", out _));
            Assert.False(wire.RootElement.TryGetProperty("OrderStatusHistory", out _));
        }
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task SourceDialect_ReachesTheSameScalarProjection(string kind, bool update)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var writer = Client(app, WritePermission(kind));
        var payload = Payload(kind).Replace("\"Name\":\"kept\"", "Name:0x10", StringComparison.Ordinal);
        // Native source string reading preserves the hexadecimal lexeme, not its numeric value.
        using var response = await SendAsync(writer, kind, update, "/*source*/" + payload);
        Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var id = update ? Id(kind) : await CreatedIdAsync(response, kind);
        await AssertStoredAsync(app, kind, id, kind is "file" or "history" ? "kept" : "0x10");
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task KnownDiscardedInteger_IsReadBeforeProjection_AndFailsPrivately(string kind, bool update)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, WritePermission(kind));
        var payload = Payload(kind).Insert(1, "\"Id\":{\"private-source-marker\":true},");
        using var response = await SendAsync(writer, kind, update, payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-source-marker", body, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonReaderException", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("category", "\"Process\":[{\"CategoryId\":{}}]")]
    [InlineData("category", "\"Process\":[{\"Order\":[{\"Remaining\":{}}]}]")]
    [InlineData("category", "\"Process\":[{\"Order\":[{\"Subtotal\":{}}]}]")]
    [InlineData("category", "\"Process\":[{\"Order\":[{\"Turnaround\":{}}]}]")]
    [InlineData("process", "\"Category\":[]")]
    [InlineData("status", "\"OrderStatusHistory\":[{\"OrderId\":{}}]")]
    [InlineData("file", "\"Order\":[]")]
    [InlineData("history", "\"OrderStatus\":[]")]
    public async Task KnownNavigationTypeFailure_DoesNotBecomeIgnoredUnknownData(string kind, string navigation)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, WritePermission(kind));
        using var response = await SendAsync(writer, kind, true, "{" + navigation + "," + PlainPayload(kind)[1..]);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [MemberData(nameof(Actions))]
    public async Task LeadingReferenceWithExtraMembers_IsNotAnOrdinaryUnknownProperty(string kind, bool update)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, WritePermission(kind));
        using var response = await SendAsync(writer, kind, update, Payload(kind).Insert(1, "\"$ref\":\"private-reference\","));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("private-reference", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("category", true)]
    [InlineData("format", false)]
    [InlineData("process", true)]
    [InlineData("status", false)]
    [InlineData("file", true)]
    [InlineData("history", false)]
    public async Task SourceInput_DoesNotGrantAnonymousOrUnrelatedAuthority(string kind, bool anonymous)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var client = anonymous ? app.CreateClient() : Client(app, OrderPermissions.Read);
        using var response = await SendAsync(client, kind, true, Payload(kind));
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"OrderId\":null,")]
    [InlineData("\"OrderId\":\"\",")]
    [InlineData("\"OrderId\":0,")]
    public async Task FileOwner_SourceZeroWithoutParent_ReachesForeignKeyAndRollsBack(string owner)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var response = await SendAsync(writer, "file", true, "{" + owner + "\"Bucket\":\"changed\",\"ObjectName\":\"changed.step\"}");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("FK_OrderFile_Order", body, StringComparison.Ordinal);
        Assert.DoesNotContain("23503", body, StringComparison.Ordinal);
        Assert.Equal(before, await SnapshotAsync(app));
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => service.UpdateFileAsync(9005, new(0, "changed", "changed.step"), default));
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, postgres.SqlState);
        Assert.Equal("FK_OrderFile_Order", postgres.ConstraintName);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"OrderId\":null,")]
    [InlineData("\"OrderId\":\"\",")]
    [InlineData("\"OrderId\":0,")]
    public async Task FileOwner_SourceZeroWithDisposableParent_PersistsZeroNotPreviousOwner(string owner)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Order" ("ID", "ProcessID", "Name", "Quantity", "Manufactured", "AllowSocialMedia", "AllowCancellation", "AllowPayment")
                VALUES (0, 9002, 'disposable zero parent', 1, 0, false, false, false)
                """);
        }
        var before = await SnapshotAsync(app, "file", 9005);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var response = await SendAsync(writer, "file", true, "{" + owner + "\"Bucket\":\"kept\",\"ObjectName\":\"kept.step\"}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var readScope = app.Services.CreateAsyncScope();
        var row = await readScope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().SingleAsync(x => x.Id == 9005);
        Assert.Equal(0, row.OrderId);
        Assert.Equal("kept", row.Bucket);
        Assert.Equal(before, await SnapshotAsync(app, "file", 9005));
    }

    [Fact]
    public async Task DirectNullableDto_RetainsExistingOwnerFallback()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<IOrderService>().UpdateFileAsync(9005, new(null, "direct", "direct.step"), default));
        var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().SingleAsync(x => x.Id == 9005);
        Assert.Equal(9003, row.OrderId);
    }

    [Theory]
    [InlineData("process", "{\"CategoryId\":9001}")]
    [InlineData("file", "{\"OrderId\":9003,\"ObjectName\":\"kept.step\"}")]
    [InlineData("file", "{\"OrderId\":9003,\"Bucket\":\"kept\"}")]
    public async Task ProjectedDto_StillReceivesImplicitRequiredValidation(string kind, string payload)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, WritePermission(kind));
        using var response = await SendAsync(writer, kind, true, payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task RootOrderInputAndDirectStj_AreNotSelectedByThisFormatter()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.Update);
        using var response = await writer.PutAsync("/orders/9003", new StringContent("{ProcessId:9002,Quantity:1,Name:'kept'}", Encoding.UTF8, "application/json"));
        // This six-family formatter still does not select root Orders. The
        // separate root source formatter now admits its legacy body grammar.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertCategoryRequest>("{Name:'kept'}"));
        Assert.Null(JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"Bucket\":\"b\",\"ObjectName\":\"o\"}")!.OrderId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("   ")]
    public async Task NativeNoValue_StillBecomesBadRequestWithoutWrites(string payload)
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var response = await SendAsync(writer, "category", false, payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task Utf16Input_UsesNativeCharsetSelectionAndSourceReading()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var content = new StringContent(Payload("category"), Encoding.Unicode, "application/json");
        using var response = await writer.PostAsync("/orders/categories", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await AssertStoredAsync(app, "category", await CreatedIdAsync(response, "category"));
    }

    [Fact]
    public async Task UnsupportedCharset_Remains415WithoutWrites()
    {
        await using var app = await fixture.AppAsync(null);
        await SeedAsync(app);
        var before = await SnapshotAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var content = new StringContent(Payload("category"), Encoding.UTF8, "application/json");
        content.Headers.ContentType!.CharSet = "iso-8859-1";
        using var response = await writer.PostAsync("/orders/categories", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static int Id(string kind) => kind switch { "category" => 9001, "format" => 9004, "process" => 9002, "status" => 9006, "file" => 9005, _ => 9009 };
    private static string Route(string kind) => kind switch { "category" => "/orders/categories", "format" => "/orders/fileformats", "process" => "/orders/processes", "status" => "/orderstatuses", "file" => "/orders/files", _ => "/orderstatuses/histories" };
    private static string WritePermission(string kind) => kind switch { "status" or "history" => OrderPermissions.StatusWrite, "file" => OrderPermissions.FilesWrite, _ => OrderPermissions.CatalogWrite };
    private static string ReadPermission(string kind) => kind == "status" ? OrderPermissions.StatusRead : OrderPermissions.CatalogRead;
    private static string PlainPayload(string kind) => kind switch
    {
        "format" => "{\"Name\":\"kept\",\"Extension\":\".kept\"}",
        "process" => "{\"CategoryId\":9001,\"Name\":\"kept\"}",
        "file" => "{\"OrderId\":9003,\"Bucket\":\"kept\",\"ObjectName\":\"kept.step\"}",
        "history" => "{\"OrderId\":9003,\"OrderStatusId\":9006}",
        _ => "{\"Name\":\"kept\"}",
    };
    private static string Payload(string kind) => kind switch
    {
        "category" => "{\"Name\":\"kept\",\"Name\":null,\"Process\":[{\"CategoryId\":9001,\"Name\":\"inert\"}]}",
        "format" => "{\"Name\":\"kept\",\"Name\":null,\"Extension\":\".kept\",\"Extension\":null}",
        "process" => "{\"CategoryId\":9001,\"CategoryId\":null,\"Name\":\"kept\",\"Name\":null,\"Category\":{\"Name\":\"inert\"}}",
        "status" => "{\"Name\":\"kept\",\"Name\":null,\"OrderStatusHistory\":[{\"OrderId\":9003,\"OrderStatusId\":9006}]}",
        "file" => "{\"OrderId\":9003,\"OrderId\":null,\"Bucket\":\"kept\",\"Bucket\":null,\"ObjectName\":\"kept.step\",\"Order\":{\"ProcessId\":9002}}",
        _ => "{\"OrderId\":9003,\"OrderId\":null,\"OrderStatusId\":9006,\"OrderStatusId\":null,\"OrderStatus\":{\"Name\":\"inert\"}}",
    };
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string kind, bool update, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return update ? await client.PutAsync(Route(kind) + "/" + Id(kind), content) : await client.PostAsync(Route(kind), content);
    }
    private static async Task<int> CreatedIdAsync(HttpResponseMessage response, string kind)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty(kind == "format" ? "id" : "Id").GetInt32();
    }
    private static async Task AssertStoredAsync(WebApplicationFactory<Program> app, string kind, int id, string expected = "kept")
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        switch (kind)
        {
            case "category": Assert.Equal(expected, (await db.Categories.AsNoTracking().SingleAsync(x => x.Id == id)).Name); break;
            case "format":
                var format = await db.FileFormats.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.Equal(expected, format.Name); Assert.Equal(".kept", format.Extension); break;
            case "process":
                var process = await db.Processes.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.Equal(expected, process.Name); Assert.Equal(9001, process.CategoryId); break;
            case "status": Assert.Equal(expected, (await statuses.Statuses.AsNoTracking().SingleAsync(x => x.Id == id)).Name); break;
            case "file":
                var file = await db.Files.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.Equal("kept", file.Bucket); Assert.Equal("kept.step", file.ObjectName); Assert.Equal(9003, file.OrderId); break;
            default:
                var history = await statuses.History.AsNoTracking().SingleAsync(x => x.Id == id);
                Assert.Equal(9003, history.OrderId); Assert.Equal(9006, history.OrderStatusId); break;
        }
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

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, string? kind = null, int id = -1)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().Where(value => kind != "process" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().Where(value => kind != "category" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().Where(value => kind != "format" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().Where(value => kind != "file" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().Where(value => kind != "status" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().Where(value => kind != "history" || value.Id != id).OrderBy(value => value.Id).ToArrayAsync(),
        });
    }
}
