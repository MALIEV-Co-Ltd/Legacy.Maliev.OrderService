using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OriginalOrder = Maliev.OrderService.Tests.SourceOrderReads.Order;
using OriginalPage = Maliev.OrderService.Tests.SourceOrderReads.PaginatedListWebApi<Maliev.OrderService.Tests.SourceOrderReads.Order>;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderReadResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static int nextOrderId = 700_000;

    [Theory]
    [InlineData("detail", 0)]
    [InlineData("detail", 1)]
    [InlineData("detail", 2)]
    [InlineData("detail", 3)]
    [InlineData("all", 0)]
    [InlineData("all", 1)]
    [InlineData("all", 2)]
    [InlineData("all", 3)]
    [InlineData("pending", 0)]
    [InlineData("pending", 1)]
    [InlineData("pending", 2)]
    [InlineData("pending", 3)]
    [InlineData("customer", 0)]
    [InlineData("customer", 1)]
    [InlineData("customer", 2)]
    [InlineData("customer", 3)]
    public async Task ReadsMatchOriginalEntityAndEnvelopeWithNullableDates(string route, int nullDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var id = await SeedAsync(db, nullDates, route == "pending", route == "customer");
        var before = await SnapshotAsync(db);
        var expected = await ExpectedAsync(db, route, id, 1);
        using var client = Client(app, Permission(route));
        using var response = await client.GetAsync(Path(route, id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertSource(body, expected);
        using var raw = JsonDocument.Parse(body);
        var item = route == "detail" ? raw.RootElement : Assert.Single(raw.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("id").GetInt32());
        Assert.Empty(item.GetProperty("orderFile").EnumerateArray());
        Assert.False(item.TryGetProperty("process", out _));
        Assert.False(item.TryGetProperty("Id", out _));
        using var consumer = new StringContent(body, Encoding.UTF8, "application/json");
        if (route == "detail")
            Assert.Equal(id, (await consumer.ReadFromJsonAsync<OrderResponse>())!.Id);
        else
        {
            var page = (await consumer.ReadFromJsonAsync<PaginatedResponse<OrderResponse>>())!;
            Assert.Equal(id, Assert.Single(page.Items).Id);
            Assert.Equal(route == "all" ? 3 : 2, page.TotalRecords);
            Assert.True(page.HasNextPage);
            Assert.False(page.HasPreviousPage);
        }
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("pending")]
    [InlineData("customer")]
    public async Task LaterPagePreservesOriginalCountsFlagsAndSelectedIds(string route)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var id = await SeedAsync(db, 0, route == "pending");
        using var client = Client(app, Permission(route));
        using var response = await client.GetAsync(Path(route, id, 2));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        AssertSource(body, await ExpectedAsync(db, route, id, 2));
        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(route == "all", document.RootElement.GetProperty("hasNextPage").GetBoolean());
    }

    [Fact]
    public async Task NativeFreshFindLeavesStoredProcessAndFilesUnloaded()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var id = await SeedAsync(db, 0, false);
        db.ChangeTracker.Clear();
        Assert.Empty(db.ChangeTracker.Entries());
        var order = (await db.Orders.FindAsync(id))!;
        Assert.Null(order.Process);
        Assert.Empty(order.Files);
        Assert.False(db.Entry(order).Reference(value => value.Process).IsLoaded);
        Assert.False(db.Entry(order).Collection(value => value.Files).IsLoaded);
        Assert.Equal(2, await db.Files.CountAsync(value => value.OrderId == id));
        Assert.Empty(order.Files);
        Assert.Null(Original(order).Process);
        Assert.Empty(Original(order).OrderFile);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("all")]
    [InlineData("pending")]
    [InlineData("customer")]
    public async Task AnonymousAndWrongPermissionReadsDoNotDiscloseOrMutate(string route)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var id = await SeedAsync(db, 0, route == "pending");
        var before = await SnapshotAsync(db);
        foreach (var (permission, status) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized),
            (OrderPermissions.CatalogRead, HttpStatusCode.Forbidden),
        })
        {
            using var client = Client(app, permission);
            using var response = await client.GetAsync(Path(route, id));
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Fact]
    public async Task EmptyOriginalReadsRemainNotFound()
    {
        await using var app = await fixture.AppAsync(null);
        foreach (var route in new[] { "detail", "all", "pending", "customer" })
        {
            using var client = Client(app, Permission(route));
            using var response = await client.GetAsync(Path(route, 999999));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public void OriginalOracleRetainsTwentySevenScalarsAndInitializedEmptyFiles()
    {
        var original = Original(Row(new Process { Id = 4 }, false, false));
        original.Id = 7;
        original.Remaining = 2;
        original.Subtotal = 35.17m;
        original.Turnaround = 4;
        using var full = JsonDocument.Parse(SerializeOriginal(original));
        Assert.Equal(28, full.RootElement.EnumerateObject().Count());
        Assert.Empty(full.RootElement.GetProperty("orderFile").EnumerateArray());
        Assert.False(full.RootElement.TryGetProperty("process", out _));
        Assert.IsType<HashSet<global::Maliev.OrderService.Tests.SourceOrderReads.OrderFile>>(original.OrderFile);
        original.Name = null;
        original.CreatedDate = null;
        using var nullable = JsonDocument.Parse(SerializeOriginal(original));
        Assert.False(nullable.RootElement.TryGetProperty("name", out _));
        Assert.False(nullable.RootElement.TryGetProperty("createdDate", out _));
        Assert.True(nullable.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task ServedReadSchemasPreserveOptionalScalarsAndSeparateInternalContracts()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var schema = root.GetProperty("components").GetProperty("schemas").GetProperty("OrderReadResponse");
        var names = schema.GetProperty("properties").EnumerateObject().Select(value => value.Name).ToArray();
        Assert.Equal(28, names.Length);
        Assert.Contains("orderFile", names);
        Assert.DoesNotContain("process", names);
        var required = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        foreach (var property in new[] { "id", "processId", "quantity", "manufactured", "allowSocialMedia", "allowCancellation", "allowPayment" })
            Assert.Contains(property, required);
        foreach (var optional in new[] { "customerId", "employeeId", "name", "description", "materialId", "surfaceFinishId", "colorId", "remaining", "unitPrice", "discountPercent", "subtotal", "currencyId", "leadTime", "promisedDate", "finishedDate", "turnaround", "comment", "trackingNumber", "createdDate", "modifiedDate" })
            Assert.DoesNotContain(optional, required);
        foreach (var (path, type) in new[] { ("/Orders/{id}", "OrderReadResponse"), ("/Orders", "OrderPageReadResponse"), ("/Orders/pending", "OrderPageReadResponse"), ("/Orders/customers/{customerId}", "OrderPageReadResponse") })
        {
            var operation = Assert.Single(root.GetProperty("paths").EnumerateObject(), item => item.Name.Equals(path, StringComparison.OrdinalIgnoreCase)).Value.GetProperty("get");
            Assert.Equal("#/components/schemas/" + type, operation.GetProperty("responses").GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        }
        using var internalOrder = JsonDocument.Parse(JsonSerializer.Serialize(new OrderResponse(1, null, null, null, null, 2, null, null, null, 1, 0, 1, null, null, null, null, null, null, null, null, null, false, false, false, null, null, null)));
        Assert.True(internalOrder.RootElement.TryGetProperty("Id", out _));
        Assert.False(internalOrder.RootElement.TryGetProperty("id", out _));
        Assert.True(root.GetProperty("components").GetProperty("schemas").GetProperty("UpsertOrderRequest").GetProperty("properties").TryGetProperty("Name", out _));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Permission(string route) => route == "customer" ? OrderPermissions.CustomerRead : OrderPermissions.Read;
    private static string Path(string route, int id, int page = 1) => route switch
    {
        "detail" => $"/orders/{id}",
        "pending" => $"/orders/pending?index={page}&size=1",
        "customer" => $"/orders/customers/7?index={page}&size=1",
        _ => $"/orders?index={page}&size=1",
    };

    private static async Task<object> ExpectedAsync(OrderDbContext db, string route, int id, int page)
    {
        if (route == "detail") return Original(await db.Orders.AsNoTracking().SingleAsync(value => value.Id == id));
        var query = db.Orders.AsNoTracking();
        if (route == "customer") query = query.Where(value => value.CustomerId == 7);
        if (route == "pending") query = query.Where(value => value.PromisedDate != null && value.FinishedDate == null);
        var count = await query.CountAsync();
        var values = await query.OrderBy(value => value.Id).Skip(page - 1).Take(1).ToListAsync();
        return new OriginalPage(values.Select(Original).ToList(), count, page, 1);
    }

    private static async Task<int> SeedAsync(OrderDbContext db, int nullDates, bool pending, bool customerScoped = false)
    {
        var process = new Process { Name = "Stored process", Category = new Category { Name = "Stored category" } };
        var first = Row(process, nullDates == 3, pending);
        if (nullDates == 3 && !customerScoped) first.CustomerId = null;
        var second = Row(process, true, true);
        var foreign = Row(process, false, false);
        // AppAsync creates fresh databases while this class fixture shares Redis; cache keys use only Order ID.
        first.Id = Interlocked.Add(ref nextOrderId, 3);
        second.Id = first.Id + 1;
        foreign.Id = first.Id + 2;
        foreign.CustomerId = 99;
        db.Orders.Add(first);
        await db.SaveChangesAsync();
        db.Orders.AddRange(second, foreign);
        await db.SaveChangesAsync();
        db.Files.AddRange(new OrderFile { OrderId = first.Id, Bucket = "metadata", ObjectName = "one.step" }, new OrderFile { OrderId = first.Id, Bucket = "metadata", ObjectName = "two.step" });
        await db.SaveChangesAsync();
        DateTime? created = (nullDates & 1) == 0 ? new DateTime(2021, 2, 3, 4, 5, 6) : null;
        DateTime? modified = (nullDates & 2) == 0 ? new DateTime(2022, 3, 4, 5, 6, 7) : null;
        await db.Orders.Where(value => value.Id == first.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.CreatedDate, created).SetProperty(value => value.ModifiedDate, modified));
        if (nullDates == 3)
            await db.Orders.Where(value => value.Id == first.Id).ExecuteUpdateAsync(setters => setters.SetProperty(value => value.Name, (string?)null));
        db.ChangeTracker.Clear();
        return first.Id;
    }

    private static Order Row(Process process, bool nullable, bool pending) => new()
    {
        Process = process,
        CustomerId = 7,
        EmployeeId = nullable ? null : 11,
        Name = nullable ? null : "ไทย source order",
        Description = nullable ? null : "Description",
        MaterialId = nullable ? null : 12,
        SurfaceFinishId = nullable ? null : 13,
        ColorId = nullable ? null : 14,
        Quantity = 3,
        Manufactured = 1,
        UnitPrice = nullable ? null : 12.34m,
        DiscountPercent = nullable ? null : 5m,
        CurrencyId = nullable ? null : 15,
        LeadTime = nullable ? null : 4,
        PromisedDate = nullable && !pending ? null : new DateTime(2022, 3, 4),
        FinishedDate = nullable || pending ? null : new DateTime(2022, 3, 5),
        Comment = nullable ? null : "Comment",
        TrackingNumber = nullable ? null : "Tracking",
        AllowSocialMedia = true,
        AllowCancellation = false,
        AllowPayment = true,
        CreatedDate = new DateTime(2022, 3, 1, 1, 2, 3),
        ModifiedDate = new DateTime(2022, 3, 2, 4, 5, 6),
    };

    private static OriginalOrder Original(Order value) => new()
    {
        Id = value.Id,
        CustomerId = value.CustomerId,
        EmployeeId = value.EmployeeId,
        Name = value.Name,
        Description = value.Description,
        ProcessId = value.ProcessId,
        MaterialId = value.MaterialId,
        SurfaceFinishId = value.SurfaceFinishId,
        ColorId = value.ColorId,
        Quantity = value.Quantity,
        Manufactured = value.Manufactured,
        Remaining = value.Remaining,
        UnitPrice = value.UnitPrice,
        DiscountPercent = value.DiscountPercent,
        Subtotal = value.Subtotal,
        CurrencyId = value.CurrencyId,
        LeadTime = value.LeadTime,
        PromisedDate = value.PromisedDate,
        FinishedDate = value.FinishedDate,
        Turnaround = value.Turnaround,
        Comment = value.Comment,
        AllowSocialMedia = value.AllowSocialMedia,
        AllowCancellation = value.AllowCancellation,
        AllowPayment = value.AllowPayment,
        TrackingNumber = value.TrackingNumber,
        CreatedDate = value.CreatedDate,
        ModifiedDate = value.ModifiedDate,
    };

    private static string SerializeOriginal(object value)
    {
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, settings);
    }

    private static void AssertSource(string actual, object original) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(original)), JsonNode.Parse(actual)));

    private static async Task<string> SnapshotAsync(OrderDbContext db) => JsonSerializer.Serialize(new
    {
        Orders = await db.Orders.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.Name, value.CreatedDate, value.ModifiedDate, value.Quantity, value.Manufactured, value.Remaining, value.Subtotal, value.Turnaround }).ToArrayAsync(),
        Files = await db.Files.AsNoTracking().OrderBy(value => value.Id).Select(value => new { value.Id, value.OrderId, value.Bucket, value.ObjectName }).ToArrayAsync(),
        Processes = await db.Processes.CountAsync(),
        Categories = await db.Categories.CountAsync(),
        DeletionIntents = await db.DeletionIntents.CountAsync(),
    });
}
