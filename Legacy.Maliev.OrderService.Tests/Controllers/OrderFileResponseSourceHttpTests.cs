using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalFile = Maliev.OrderService.Tests.SourceOrderFiles.OrderFile;
using OriginalOrder = Maliev.OrderService.Tests.SourceOrderFiles.Order;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task DetailAndListMatchOriginalUnloadedParentAndNullableDates(int nullDates)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var parent = await SeedAsync(db, false);
        var row = new OrderFile { OrderId = parent.Id, Bucket = "  metadata  ", ObjectName = "ไทย/part.step" };
        db.Files.Add(row);
        await db.SaveChangesAsync();
        DateTime? created = (nullDates & 1) == 0 ? new DateTime(2021, 2, 3, 4, 5, 6) : null;
        DateTime? modified = (nullDates & 2) == 0 ? new DateTime(2022, 3, 4, 5, 6, 7) : null;
        await db.Files.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(item => item.CreatedDate, created).SetProperty(item => item.ModifiedDate, modified));
        db.ChangeTracker.Clear();
        var before = await SnapshotAsync(db);
        var stored = (await db.Files.FindAsync(row.Id))!;
        Assert.Null(stored.Order);
        Assert.False(db.Entry(stored).Reference(item => item.Order).IsLoaded);
        var original = Original(stored);
        using var reader = Client(app, OrderPermissions.FilesRead);
        using var detail = await reader.GetAsync($"/orders/files/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        AssertSource(await detail.Content.ReadAsStringAsync(), original);
        using var list = await reader.GetAsync($"/orders/{parent.Id}/files");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var document = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        AssertSource(Assert.Single(document.RootElement.EnumerateArray()).GetRawText(), original);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateMatchesFreshSourceGraphAndUtcThenLocationMatchesStoredGet(bool nullableParent)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var parent = await SeedAsync(db, nullableParent);
        var oldFile = new OrderFile { OrderId = parent.Id, Bucket = "existing", ObjectName = "existing.step" };
        db.Files.Add(oldFile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var beforeParent = JsonSerializer.Serialize(await db.Orders.AsNoTracking().SingleAsync());
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var created = await writer.PostAsync($"/orders/{parent.Id}/files?bucket=metadata&objectName=part.step", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(created.Headers.Location);
        var body = await created.Content.ReadAsStringAsync();
        using var consumerBody = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var value = (await consumerBody.ReadFromJsonAsync<OrderFileResponse>())!;
        var original = new OriginalFile
        {
            Id = value.Id,
            OrderId = parent.Id,
            Bucket = "metadata",
            ObjectName = "part.step",
            CreatedDate = clock.GetUtcNow().UtcDateTime,
            ModifiedDate = clock.GetUtcNow().UtcDateTime,
        };
        // Original Find(parent) then Add(child) performs relationship fixup, but never loads siblings or Process.
        await using var source = SourceContext(db);
        var sourceParent = (await source.Set<OriginalOrder>().FindAsync(parent.Id))!;
        Assert.Empty(sourceParent.OrderFile);
        Assert.Null(sourceParent.Process);
        source.Add(original);
        Assert.Same(sourceParent, original.Order);
        Assert.Same(original, Assert.Single(sourceParent.OrderFile));
        AssertSource(body, original);
        using var reader = Client(app, OrderPermissions.FilesRead);
        using var detail = await reader.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var persisted = await db.Files.AsNoTracking().SingleAsync(item => item.Id == value.Id);
        Assert.Equal(DateTimeKind.Unspecified, persisted.CreatedDate!.Value.Kind);
        AssertSource(await detail.Content.ReadAsStringAsync(), Original(persisted));
        Assert.Equal(value, await detail.Content.ReadFromJsonAsync<OrderFileResponse>());
        Assert.Equal(beforeParent, JsonSerializer.Serialize(await db.Orders.AsNoTracking().SingleAsync()));
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var duplicate = await service.CreateFileAsync(parent.Id, "metadata", "part.step", default);
        var reread = await service.GetFileAsync(value.Id, default);
        Assert.NotNull(duplicate);
        Assert.NotNull(reread);
        Assert.Equal(reread, duplicate);
        Assert.Equal(reread.GetHashCode(), duplicate.GetHashCode());
        Assert.Equal(JsonSerializer.Serialize(reread), JsonSerializer.Serialize(duplicate));
        Assert.Equal(2, await db.Files.CountAsync());
        Assert.Equal("existing.step", (await db.Files.AsNoTracking().SingleAsync(item => item.Id == oldFile.Id)).ObjectName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnauthorizedResponsesCannotCreateOrExposeFiles(bool authenticated)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var parent = await SeedAsync(db, false);
        using var client = app.CreateClient();
        if (authenticated) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        using var created = await client.PostAsync($"/orders/{parent.Id}/files?bucket=metadata&objectName=part.step", null);
        using var detail = await client.GetAsync("/orders/files/123");
        using var list = await client.GetAsync($"/orders/{parent.Id}/files");
        var expected = authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
        Assert.Equal(expected, created.StatusCode);
        Assert.Equal(expected, detail.StatusCode);
        Assert.Equal(expected, list.StatusCode);
        Assert.Empty(await db.Files.ToArrayAsync());
    }

    [Fact]
    public void InternalFileDtoRetainsPascalCaseAndHasNoSourceNavigation()
    {
        var value = new OrderFileResponse(1, 2, "bucket", "part.step", null, null);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        Assert.Equal(new[] { "Bucket", "CreatedDate", "Id", "ModifiedDate", "ObjectName", "OrderId" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ServedSchemaMatchesFileResponsesAndRetainsRequestNames()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var schemas = document["components"]!["schemas"]!;
        var file = schemas["OrderFileReadResponse"]!;
        Assert.NotNull(file);
        Assert.Equal(new[] { "bucket", "createdDate", "id", "modifiedDate", "objectName", "order", "orderId" },
            file["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        var required = file["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray();
        Assert.Contains("id", required);
        Assert.Contains("orderId", required);
        Assert.DoesNotContain("createdDate", required);
        Assert.DoesNotContain("modifiedDate", required);
        Assert.DoesNotContain("order", required);
        Assert.NotNull(schemas["UpsertOrderFileRequest"]!["properties"]!["Bucket"]);
        foreach (var (route, method, status) in new[]
        {
            ("/orders/Files/{id}", "get", "200"),
            ("/orders/{orderId}/files", "post", "201"),
        })
        {
            var path = Assert.Single(document["paths"]!.AsObject(), entry =>
                string.Equals(entry.Key, route, StringComparison.OrdinalIgnoreCase)).Value!;
            Assert.Equal("#/components/schemas/OrderFileReadResponse",
                path[method]!["responses"]![status]!["content"]!["application/json"]!["schema"]!["$ref"]!.GetValue<string>());
        }
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static OriginalFile Original(OrderFile value) => new()
    {
        Id = value.Id,
        OrderId = value.OrderId,
        Bucket = value.Bucket,
        ObjectName = value.ObjectName,
        CreatedDate = value.CreatedDate,
        ModifiedDate = value.ModifiedDate,
    };

    private static void AssertSource(string actual, OriginalFile original)
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var expected = Newtonsoft.Json.JsonConvert.SerializeObject(original, options.SerializerSettings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), $"Expected source response {expected}; actual {actual}");
    }

    private static async Task<Order> SeedAsync(OrderDbContext db, bool nullable)
    {
        var row = new Order
        {
            Name = nullable ? null : "ไทย source parent",
            Description = nullable ? null : "Original metadata",
            Process = new Process
            {
                Name = "Process",
                Category = new Category { Name = "Category" },
            },
            CustomerId = nullable ? null : 11,
            EmployeeId = nullable ? null : 12,
            MaterialId = nullable ? null : 13,
            SurfaceFinishId = nullable ? null : 14,
            ColorId = nullable ? null : 15,
            Quantity = 3,
            Manufactured = 1,
            UnitPrice = nullable ? null : 12.34m,
            DiscountPercent = nullable ? null : 5m,
            CurrencyId = nullable ? null : 16,
            LeadTime = nullable ? null : 4,
            PromisedDate = nullable ? null : new DateTime(2022, 3, 4),
            FinishedDate = nullable ? null : new DateTime(2022, 3, 5),
            Comment = nullable ? null : "Comment",
            TrackingNumber = nullable ? null : "tracking",
            AllowCancellation = true,
            AllowPayment = false,
            AllowSocialMedia = true,
            CreatedDate = new DateTime(2022, 3, 1, 1, 2, 3),
            ModifiedDate = new DateTime(2022, 3, 2, 4, 5, 6),
        };
        db.Orders.Add(row);
        await db.SaveChangesAsync();
        if (nullable) await db.Orders.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Name, (string?)null).SetProperty(item => item.CreatedDate, (DateTime?)null)
            .SetProperty(item => item.ModifiedDate, (DateTime?)null));
        return row;
    }

    private static async Task<string> SnapshotAsync(OrderDbContext db) => JsonSerializer.Serialize(
        await db.Files.AsNoTracking().OrderBy(item => item.Id).Select(item => new OrderFileResponse(
            item.Id, item.OrderId, item.Bucket, item.ObjectName, item.CreatedDate, item.ModifiedDate)).ToArrayAsync());

    private static OriginalContext SourceContext(OrderDbContext db) => new(new DbContextOptionsBuilder<OriginalContext>()
        .UseNpgsql(db.Database.GetConnectionString()).Options);

    private sealed class OriginalContext(DbContextOptions<OriginalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var order = builder.Entity<OriginalOrder>();
            order.ToTable("Order");
            order.HasKey(value => value.Id);
            order.Ignore(value => value.Process);
            foreach (var property in order.Metadata.GetProperties())
            {
                if (property.Name.EndsWith("Id", StringComparison.Ordinal)) property.SetColumnName(property.Name[..^2] + "ID");
                if (property.ClrType == typeof(DateTime?)) property.SetColumnType(
                    property.Name is "PromisedDate" or "FinishedDate" ? "date" : "timestamp without time zone");
            }
            var file = builder.Entity<OriginalFile>();
            file.ToTable("OrderFile");
            file.HasKey(value => value.Id);
            file.Property(value => value.Id).HasColumnName("ID");
            file.Property(value => value.OrderId).HasColumnName("OrderID");
            file.Property(value => value.CreatedDate).HasColumnType("timestamp without time zone");
            file.Property(value => value.ModifiedDate).HasColumnType("timestamp without time zone");
            file.HasOne(value => value.Order).WithMany(value => value.OrderFile).HasForeignKey(value => value.OrderId);
        }
    }
}
