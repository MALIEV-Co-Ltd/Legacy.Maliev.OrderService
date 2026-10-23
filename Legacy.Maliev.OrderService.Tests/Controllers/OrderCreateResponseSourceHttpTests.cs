using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Api.Controllers;
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
using OriginalOrder = Maliev.OrderService.Tests.SourceOrderFiles.Order;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderCreateResponseSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static int nextOrderId = 900000;

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "2026-10-24")]
    [InlineData(true, "2026-10-24")]
    [InlineData(false, "2026-10-24T13:14:15.1234567")]
    [InlineData(true, "2026-10-24T13:14:15.1234567")]
    [InlineData(false, "2026-10-24T13:14:15.1234567Z")]
    [InlineData(true, "2026-10-24T13:14:15.1234567Z")]
    [InlineData(false, "2026-10-24T13:14:15.1234567+07:00")]
    [InlineData(true, "2026-10-24T13:14:15.1234567+07:00")]
    public async Task FreshCreateMatchesOriginalEntityAndInputDatesWhileLocationRetainsStoredDates(bool nullable, string? date)
    {
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var before = await SnapshotAsync(db, statuses, deadline.Token);
        var payload = Payload(processId, nullable, date);
        var input = BindOriginal(payload);
        using var writer = Client(app, OrderPermissions.Create);
        using var response = await PostAsync(writer, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        using var consumerBody = new StringContent(body, Encoding.UTF8, "application/json");
        var consumer = (await consumerBody.ReadFromJsonAsync<OrderResponse>(deadline.Token))!;
        Assert.NotEqual(input.Id, consumer.Id);
        var stored = await db.Orders.AsNoTracking().SingleAsync(row => row.Id == consumer.Id, deadline.Token);
        Assert.Equal(input.PromisedDate?.Date, stored.PromisedDate);
        Assert.Equal(input.FinishedDate?.Date, stored.FinishedDate);
        Assert.Equal(DateTimeKind.Unspecified, stored.CreatedDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, stored.ModifiedDate!.Value.Kind);
        Assert.Equal(before, await SnapshotAsync(db, statuses, deadline.Token, stored.Id));
        var expected = FreshOriginal(input, stored, clock.GetUtcNow().UtcDateTime);
        AssertSource(body, expected);
        Assert.Equal(expected.PromisedDate, consumer.PromisedDate);
        Assert.Equal(expected.PromisedDate?.Kind, consumer.PromisedDate?.Kind);
        Assert.Equal(DateTimeKind.Utc, consumer.CreatedDate!.Value.Kind);
        using var reader = Client(app, OrderPermissions.Read);
        using var detail = await reader.GetAsync(response.Headers.Location, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var loaded = (await detail.Content.ReadFromJsonAsync<OrderResponse>(deadline.Token))!;
        Assert.Equal(stored.PromisedDate, loaded.PromisedDate);
        Assert.Equal(stored.FinishedDate, loaded.FinishedDate);
        Assert.Equal(DateTimeKind.Unspecified, loaded.CreatedDate!.Value.Kind);
    }

    [Theory]
    [InlineData(false, "2026-10-24T13:14:15.1234567")]
    [InlineData(true, "2026-10-24T13:14:15.1234567")]
    [InlineData(false, "2026-10-24T13:14:15.1234567Z")]
    [InlineData(true, "2026-10-24T13:14:15.1234567Z")]
    [InlineData(false, "2026-10-24T13:14:15.1234567+07:00")]
    [InlineData(true, "2026-10-24T13:14:15.1234567+07:00")]
    public async Task OriginalClrExplicitDatesAndNameDefaultSurviveSaveWhileFreshReadUsesCalendarStorage(bool nullName, string date)
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var original = BindOriginal(Payload(processId, nullName, date));
        Assert.Equal(nullName ? null : "ไทย source order", original.Name);
        original.Id = 0;
        original.Process = null;
        original.CreatedDate = Clock().GetUtcNow().UtcDateTime;
        original.ModifiedDate = original.CreatedDate;
        await using var source = new OriginalContext(new DbContextOptionsBuilder<OriginalContext>()
            .UseNpgsql(db.Database.GetConnectionString()).Options);
        source.Add(original);
        var promised = original.PromisedDate;
        var created = original.CreatedDate;
        await source.SaveChangesAsync(deadline.Token);
        Assert.Equal(nullName ? "unnamed" : "ไทย source order", original.Name);
        Assert.Equal(promised, original.PromisedDate);
        Assert.Equal(promised!.Value.Kind, original.PromisedDate!.Value.Kind);
        Assert.Equal(created, original.CreatedDate);
        Assert.Equal(DateTimeKind.Utc, original.CreatedDate!.Value.Kind);
        Assert.Equal(original.Quantity - original.Manufactured, original.Remaining);
        var loaded = await source.Set<OriginalOrder>().AsNoTracking().SingleAsync(row => row.Id == original.Id, deadline.Token);
        Assert.Equal(promised.Value.Date, loaded.PromisedDate);
        Assert.Equal(DateTimeKind.Unspecified, loaded.PromisedDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Unspecified, loaded.CreatedDate!.Value.Kind);
        Assert.NotEqual(SerializeOriginal(original), SerializeOriginal(loaded));
    }

    [Theory]
    [InlineData("2026-10-24T13:14:15.1234567")]
    [InlineData("2026-10-24T13:14:15.1234567Z")]
    public async Task DirectRepositoryCreationRetainsStoredInternalDtoEqualityAndSerialization(string date)
    {
        await using var app = await AppAsync(Clock());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var request = JsonSerializer.Deserialize<UpsertOrderRequest>(Payload(processId, false, date))!;
        var created = await service.CreateOrderAsync(request, deadline.Token);
        var loaded = (await service.GetOrderAsync(created.Id, deadline.Token))!;
        Assert.Equal(loaded, created);
        Assert.Equal(loaded.GetHashCode(), created.GetHashCode());
        Assert.Equal(JsonSerializer.Serialize(loaded), JsonSerializer.Serialize(created));
        Assert.Equal(DateTimeKind.Unspecified, created.CreatedDate!.Value.Kind);
        Assert.Equal(created.PromisedDate!.Value.Date, created.PromisedDate);
    }

    [Fact]
    public async Task HeaderReplayRetainsExactFreshResponseAfterClockAndStoredDatesChange()
    {
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(db, deadline.Token);
        var payload = Payload(id, false, "2026-10-24T13:14:15.1234567Z");
        using var client = Client(app, OrderPermissions.Create);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var first = await PostAsync(client, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync(deadline.Token);
        var stored = await db.Orders.AsNoTracking().SingleAsync(deadline.Token);
        var expected = FreshOriginal(BindOriginal(payload), stored, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromDays(2));
        await db.Orders.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.PromisedDate, (DateTime?)new DateTime(2027, 1, 1)), deadline.Token);
        var before = await SnapshotAsync(db, statuses, deadline.Token);
        using var replay = await PostAsync(client, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(firstBody, await replay.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(before, await SnapshotAsync(db, statuses, deadline.Token));
        AssertSource(firstBody, expected);
    }

    [Fact]
    public async Task DurableReplayUsesPersistedDatesAndNeverTheNewRequestOrClock()
    {
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(db, deadline.Token);
        var key = Guid.NewGuid().ToString("N");
        using var client = Client(app, OrderPermissions.Create);
        using var first = await PostAsync(client, Payload(id, false, "2026-10-24T13:14:15Z", key), deadline.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var stored = await db.Orders.AsNoTracking().SingleAsync(deadline.Token);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var prior = (await service.GetOrderAsync(stored.Id, deadline.Token))!;
        var before = await SnapshotAsync(db, statuses, deadline.Token);
        clock.Advance(TimeSpan.FromDays(7));
        using var replay = await PostAsync(client, Payload(id, true, "2030-05-06T03:04:05Z", key), deadline.Token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var body = await replay.Content.ReadAsStringAsync(deadline.Token);
        using var consumer = new StringContent(body, Encoding.UTF8, "application/json");
        Assert.Equal(prior, await consumer.ReadFromJsonAsync<OrderResponse>(deadline.Token));
        Assert.Equal(before, await SnapshotAsync(db, statuses, deadline.Token));
        AssertSource(body, FreshOriginal(BindOriginal(Payload(id, false, null)), stored, stored.CreatedDate!.Value,
            stored.PromisedDate, stored.FinishedDate, stored.ModifiedDate));
    }

    [Fact]
    public async Task UnauthorizedAndChangedHeaderRequestsPreserveBothDatabases()
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(db, deadline.Token);
        var payload = Payload(id, false, null);
        var before = await SnapshotAsync(db, statuses, deadline.Token);
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized), (OrderPermissions.Read, HttpStatusCode.Forbidden),
        })
        {
            using var rejected = Client(app, permission);
            using var response = await PostAsync(rejected, payload, deadline.Token);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(db, statuses, deadline.Token));
        using var writer = Client(app, OrderPermissions.Create);
        writer.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var first = await PostAsync(writer, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        before = await SnapshotAsync(db, statuses, deadline.Token);
        using var conflict = await PostAsync(writer, Payload(id, false, "2030-05-06"), deadline.Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(before, await SnapshotAsync(db, statuses, deadline.Token));
    }

    [Fact]
    public async Task ExistingAndSourceResponseEnvelopesRetainBidirectionalCacheCompatibility()
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var stored = await service.CreateOrderAsync(JsonSerializer.Deserialize<UpsertOrderRequest>(Payload(processId, false, null))!, deadline.Token);
        var cache = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var oldKey = Guid.NewGuid().ToString("N");
        var old = await cache.AcquireAsync<OrderResponse>("source-cache-control", oldKey, "same", deadline.Token);
        await cache.CompleteAsync("source-cache-control", oldKey, "same", old.ReservationId!, stored, deadline.Token);
        var newReader = await cache.AcquireAsync<OrderReadResponse>("source-cache-control", oldKey, "same", deadline.Token);
        Assert.Equal(IdempotencyAcquireState.Replay, newReader.State);
        Assert.Equal(OrderReadResponse.From(stored), newReader.Response);
        var newKey = Guid.NewGuid().ToString("N");
        var current = await cache.AcquireAsync<OrderReadResponse>("source-cache-control", newKey, "same", deadline.Token);
        await cache.CompleteAsync("source-cache-control", newKey, "same", current.ReservationId!, OrderReadResponse.From(stored), deadline.Token);
        var oldReader = await cache.AcquireAsync<OrderResponse>("source-cache-control", newKey, "same", deadline.Token);
        Assert.Equal(IdempotencyAcquireState.Replay, oldReader.State);
        Assert.Equal(stored, oldReader.Response);
    }

    [Fact]
    public async Task ServedCreationSchemaSeparatesSourceResponseFromPascalRequest()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var response = await client.GetAsync("/order/openapi/v1.json", deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var root = document.RootElement;
        var post = Assert.Single(root.GetProperty("paths").EnumerateObject(), row => row.Name.Equals("/orders", StringComparison.OrdinalIgnoreCase)).Value.GetProperty("post");
        Assert.Equal("#/components/schemas/OrderReadResponse", post.GetProperty("responses").GetProperty("201")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var request = root.GetProperty("components").GetProperty("schemas").GetProperty("UpsertOrderRequest").GetProperty("properties");
        Assert.True(request.TryGetProperty("PromisedDate", out _));
        Assert.True(request.TryGetProperty("OperationKey", out _));
    }

    private Task<WebApplicationFactory<Program>> AppAsync(TimeProvider clock) => fixture.AppAsync(null, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(clock);
    });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero).AddTicks(1234567));

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string payload, CancellationToken token)
    {
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        return await client.PostAsync("/orders", body, token);
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Payload(int processId, bool nullable, string? date, string? operationKey = null) => JsonSerializer.Serialize(new
    {
        Id = 987654, CustomerId = nullable ? (int?)null : 11, EmployeeId = nullable ? (int?)null : 12,
        Name = nullable ? null : "ไทย source order", Description = nullable ? null : "Source description", ProcessId = processId,
        MaterialId = nullable ? (int?)null : 13, SurfaceFinishId = nullable ? (int?)null : 14, ColorId = nullable ? (int?)null : 15,
        Quantity = 3, Manufactured = 1, Remaining = -987, UnitPrice = nullable ? (decimal?)null : 12.34m,
        DiscountPercent = nullable ? (decimal?)null : 5m, Subtotal = -987m, CurrencyId = nullable ? (int?)null : 16,
        LeadTime = nullable ? (int?)null : 4, PromisedDate = date, FinishedDate = date, Turnaround = -987,
        Comment = nullable ? null : "Comment", AllowSocialMedia = true, AllowCancellation = true, AllowPayment = false,
        TrackingNumber = nullable ? null : "tracking", CreatedDate = "2001-01-01T00:00:00Z", ModifiedDate = "2001-01-01T00:00:00Z",
        Process = new { Id = 999, Name = "Ignored" }, OrderFile = new[] { new { Id = 999, Bucket = "ignored", ObjectName = "ignored" } },
        OperationKey = operationKey,
    });

    private static OriginalOrder BindOriginal(string body) => Newtonsoft.Json.JsonConvert.DeserializeObject<OriginalOrder>(body)!;

    private static OriginalOrder FreshOriginal(OriginalOrder input, Order stored, DateTime created,
        DateTime? promised = null, DateTime? finished = null, DateTime? modified = null) => new()
    {
        // Initial source OrderContext marks Name generated-on-add with (N'unnamed'); the CLR/EF control qualifies null's store default.
        Id = stored.Id, CustomerId = input.CustomerId, EmployeeId = input.EmployeeId, Name = input.Name ?? "unnamed",
        Description = input.Description, ProcessId = input.ProcessId, MaterialId = input.MaterialId,
        SurfaceFinishId = input.SurfaceFinishId, ColorId = input.ColorId, Quantity = input.Quantity, Manufactured = input.Manufactured,
        Remaining = stored.Remaining, UnitPrice = input.UnitPrice, DiscountPercent = input.DiscountPercent, Subtotal = stored.Subtotal,
        CurrencyId = input.CurrencyId, LeadTime = input.LeadTime, PromisedDate = promised ?? input.PromisedDate,
        FinishedDate = finished ?? input.FinishedDate, Turnaround = stored.Turnaround, Comment = input.Comment,
        AllowSocialMedia = input.AllowSocialMedia, AllowCancellation = input.AllowCancellation, AllowPayment = input.AllowPayment,
        TrackingNumber = input.TrackingNumber, CreatedDate = created, ModifiedDate = modified ?? created,
    };

    private static string SerializeOriginal(OriginalOrder value)
    {
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        return Newtonsoft.Json.JsonConvert.SerializeObject(value, settings);
    }

    private static void AssertSource(string actual, OriginalOrder expected) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(SerializeOriginal(expected)), JsonNode.Parse(actual)));

    private static async Task<int> SeedAsync(OrderDbContext db, CancellationToken token)
    {
        var process = new Process { Name = "Source process", Category = new Category { Name = "Source category" } };
        db.Processes.Add(process);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        // Databases are per case, but the real fixture Redis cache is shared and keys orders by ID.
        var firstOrderId = Interlocked.Add(ref nextOrderId, 100);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval(pg_get_serial_sequence('\"Order\"', 'ID'), {firstOrderId}, false)", token);
        return process.Id;
    }

    private static async Task<string> SnapshotAsync(OrderDbContext db, OrderStatusDbContext statuses, CancellationToken token, int excluded = -1) =>
        JsonSerializer.Serialize(new
        {
            Orders = await db.Orders.AsNoTracking().Where(row => row.Id != excluded).OrderBy(row => row.Id).ToArrayAsync(token),
            Processes = await db.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Categories = await db.Categories.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Files = await db.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Formats = await db.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            DeletionIntents = await db.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
        });

    private sealed class OriginalContext(DbContextOptions<OriginalContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var order = builder.Entity<OriginalOrder>();
            order.ToTable("Order");
            order.HasKey(row => row.Id);
            order.Ignore(row => row.Process);
            order.Ignore(row => row.OrderFile);
            foreach (var property in order.Metadata.GetProperties())
            {
                if (property.Name.EndsWith("Id", StringComparison.Ordinal)) property.SetColumnName(property.Name[..^2] + "ID");
                if (property.ClrType == typeof(DateTime?)) property.SetColumnType(
                    property.Name is "PromisedDate" or "FinishedDate" ? "date" : "timestamp without time zone");
            }
            order.Property(row => row.Id).ValueGeneratedOnAdd();
            // Same literal default as the original SQL Server model, applied to this disposable PostgreSQL witness only.
            order.Property(row => row.Name).HasMaxLength(100).HasDefaultValue("unnamed");
            order.Property(row => row.Remaining).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.Subtotal).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.Turnaround).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.UnitPrice).HasPrecision(18, 2);
            order.Property(row => row.DiscountPercent).HasPrecision(5, 2);
            // PostgreSQL storage adapter only; the original tracked explicit UTC values remain unchanged.
            foreach (var name in new[] { "CreatedDate", "ModifiedDate" })
                order.Property<DateTime?>(name).HasConversion(
                    value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified) : value, value => value);
        }
    }
}
