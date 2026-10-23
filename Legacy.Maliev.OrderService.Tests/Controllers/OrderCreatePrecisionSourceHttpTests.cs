using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OriginalOrder = global::Maliev.OrderService.Tests.SourceOrderFiles.Order;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderCreatePrecisionSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static int nextOrderId = 1_400_000;
    public static TheoryData<string, decimal?, decimal?, decimal?, decimal?> PrecisionCases => new()
    {
        { "price-extra", 12.3456m, null, 12.35m, null },
        { "discount-extra", null, 5.6789m, null, 5.68m },
        { "both-extra", 12.3456m, 5.6789m, 12.35m, 5.68m },
        { "nullable", null, null, null, null },
        { "exact-two-places", 12.34m, 5.67m, 12.34m, 5.67m },
    };

    [Theory, MemberData(nameof(PrecisionCases))]
    public async Task FreshResponseMatchesOriginalTrackedDecimalsWhileLocationUsesStoredValues(
        string vector, decimal? price, decimal? discount, decimal? storedPrice, decimal? storedDiscount)
    {
        _ = vector;
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var original = await SaveOriginalAsync(db, processId, price, discount, clock, deadline.Token);
        var originalStored = await db.Orders.AsNoTracking().SingleAsync(row => row.Id == original.Id, deadline.Token);
        Assert.Equal(storedPrice, originalStored.UnitPrice);
        Assert.Equal(storedDiscount, originalStored.DiscountPercent);
        var before = await SnapshotAsync(app, deadline.Token);
        using var writer = Client(app, OrderPermissions.Create);
        using var response = await PostAsync(writer, Payload(processId, price, discount), deadline.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(deadline.Token);
        var id = JsonNode.Parse(body)!["id"]!.GetValue<int>();
        Assert.NotEqual(original.Id, id);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal($"/Orders/{id.ToString(CultureInfo.InvariantCulture)}", response.Headers.Location.AbsolutePath, ignoreCase: true);
        var stored = await db.Orders.AsNoTracking().SingleAsync(row => row.Id == id, deadline.Token);
        Assert.Equal(storedPrice, stored.UnitPrice);
        Assert.Equal(storedDiscount, stored.DiscountPercent);
        Assert.Equal(original.Remaining, stored.Remaining);
        Assert.Equal(original.Subtotal, stored.Subtotal);
        Assert.Equal(original.Turnaround, stored.Turnaround);
        Assert.Equal(before, await SnapshotAsync(app, deadline.Token, id));
        using var reader = Client(app, OrderPermissions.Read);
        using var detail = await reader.GetAsync(response.Headers.Location, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var loaded = (await detail.Content.ReadFromJsonAsync<OrderResponse>(deadline.Token))!;
        Assert.Equal(storedPrice, loaded.UnitPrice);
        Assert.Equal(storedDiscount, loaded.DiscountPercent);
        Assert.Equal(stored.Subtotal, loaded.Subtotal);
        AssertOriginal(body, original, id);
        using var consumerBody = new StringContent(body, Encoding.UTF8, "application/json");
        var consumer = (await consumerBody.ReadFromJsonAsync<OrderResponse>(deadline.Token))!;
        Assert.Equal(price, consumer.UnitPrice);
        Assert.Equal(discount, consumer.DiscountPercent);
    }

    [Fact]
    public async Task HeaderReplayKeepsExactFreshPrecisionAfterStoredValuesAndClockChange()
    {
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var original = await SaveOriginalAsync(db, processId, 12.3456m, 5.6789m, clock, deadline.Token);
        using var client = Client(app, OrderPermissions.Create);
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var payload = Payload(processId, 12.3456m, 5.6789m);
        using var first = await PostAsync(client, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync(deadline.Token);
        var id = JsonNode.Parse(body)!["id"]!.GetValue<int>();
        await db.Orders.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.UnitPrice, (decimal?)90.01m)
            .SetProperty(row => row.DiscountPercent, (decimal?)10.01m), deadline.Token);
        clock.Advance(TimeSpan.FromDays(1));
        var before = await SnapshotAsync(app, deadline.Token);
        using var replay = await PostAsync(client, payload, deadline.Token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        Assert.Equal(body, await replay.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal(before, await SnapshotAsync(app, deadline.Token));
        AssertOriginal(body, original, id);
    }

    [Fact]
    public async Task DurableReplayUsesStoredPrecisionWithoutSubstitutingTheNewRequest()
    {
        var clock = Clock();
        await using var app = await AppAsync(clock);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var key = Guid.NewGuid().ToString("N");
        using var client = Client(app, OrderPermissions.Create);
        using var first = await PostAsync(client, Payload(processId, 12.3456m, 5.6789m, key), deadline.Token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var id = JsonNode.Parse(await first.Content.ReadAsStringAsync(deadline.Token))!["id"]!.GetValue<int>();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var prior = (await service.GetOrderAsync(id, deadline.Token))!;
        Assert.Equal(12.35m, prior.UnitPrice);
        Assert.Equal(5.68m, prior.DiscountPercent);
        var before = await SnapshotAsync(app, deadline.Token);
        clock.Advance(TimeSpan.FromDays(1));
        using var replay = await PostAsync(client, Payload(processId, 88.8888m, 22.2222m, key), deadline.Token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        var stored = (await replay.Content.ReadFromJsonAsync<OrderResponse>(deadline.Token))!;
        Assert.Equal(prior, stored);
        Assert.Equal(12.35m, stored.UnitPrice);
        Assert.Equal(5.68m, stored.DiscountPercent);
        Assert.Equal(before, await SnapshotAsync(app, deadline.Token));
    }

    [Fact]
    public async Task DirectServiceKeepsStoredDtoValuesEqualityHashAndDefaultJson()
    {
        await using var app = await AppAsync(Clock());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var request = JsonSerializer.Deserialize<UpsertOrderRequest>(Payload(processId, 12.3456m, 5.6789m))!;
        var created = await service.CreateOrderAsync(request, deadline.Token);
        var loaded = (await service.GetOrderAsync(created.Id, deadline.Token))!;
        Assert.Equal(12.35m, created.UnitPrice);
        Assert.Equal(5.68m, created.DiscountPercent);
        Assert.Equal(loaded, created);
        Assert.Equal(loaded.GetHashCode(), created.GetHashCode());
        Assert.Equal(JsonSerializer.Serialize(loaded), JsonSerializer.Serialize(created));
    }

    [Fact]
    public async Task AnonymousAndWrongPermissionPrecisionRequestsLeaveBothDatabasesUnchanged()
    {
        await using var app = await AppAsync(Clock());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var processId = await SeedAsync(db, deadline.Token);
        var before = await SnapshotAsync(app, deadline.Token);
        foreach (var (permission, expected) in new (string?, HttpStatusCode)[]
        {
            (null, HttpStatusCode.Unauthorized), (OrderPermissions.Read, HttpStatusCode.Forbidden),
        })
        {
            using var client = Client(app, permission);
            using var response = await PostAsync(client, Payload(processId, 12.3456m, 5.6789m), deadline.Token);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Equal(before, await SnapshotAsync(app, deadline.Token));
    }

    private Task<WebApplicationFactory<Program>> AppAsync(TimeProvider clock) => fixture.AppAsync(null, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton(clock);
    });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.Zero));

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string payload, CancellationToken token)
    {
        using var body = new StringContent(payload, Encoding.UTF8, "application/json");
        return await client.PostAsync("/orders", body, token);
    }

    private static string Payload(int processId, decimal? price, decimal? discount, string? operationKey = null) =>
        JsonSerializer.Serialize(new
        {
            CustomerId = (int?)11,
            EmployeeId = (int?)12,
            Name = "Source precision",
            Description = "Owned response witness",
            ProcessId = processId,
            MaterialId = (int?)13,
            SurfaceFinishId = (int?)14,
            ColorId = (int?)15,
            Quantity = 3,
            Manufactured = 1,
            UnitPrice = price,
            DiscountPercent = discount,
            CurrencyId = (int?)16,
            LeadTime = (int?)null,
            PromisedDate = (DateTime?)null,
            FinishedDate = (DateTime?)null,
            Comment = "Source response only",
            AllowSocialMedia = false,
            AllowCancellation = false,
            AllowPayment = false,
            TrackingNumber = (string?)null,
            OperationKey = operationKey,
        });

    private static async Task<int> SeedAsync(OrderDbContext db, CancellationToken token)
    {
        var process = new Process { Name = "Source process", Category = new Category { Name = "Source category" } };
        db.Processes.Add(process);
        await db.SaveChangesAsync(token);
        db.ChangeTracker.Clear();
        // Per-case databases share fixture Redis. Keep generated IDs distinct without changing runtime caches.
        var first = Interlocked.Add(ref nextOrderId, 100);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval(pg_get_serial_sequence('\"Order\"', 'ID'), {first}, false)", token);
        return process.Id;
    }

    private static async Task<OriginalOrder> SaveOriginalAsync(OrderDbContext db, int processId,
        decimal? price, decimal? discount, FakeTimeProvider clock, CancellationToken token)
    {
        var original = Newtonsoft.Json.JsonConvert.DeserializeObject<OriginalOrder>(Payload(processId, price, discount))!;
        original.CreatedDate = clock.GetUtcNow().UtcDateTime;
        original.ModifiedDate = original.CreatedDate;
        await using var source = new OriginalContext(new DbContextOptionsBuilder<OriginalContext>()
            .UseNpgsql(db.Database.GetConnectionString()).Options);
        source.Add(original);
        await source.SaveChangesAsync(token);
        // These assertions qualify the actual tracked explicit values after PostgreSQL persistence.
        // They do not claim execution of the original SQL Server runtime.
        Assert.Equal(price, original.UnitPrice);
        Assert.Equal(discount, original.DiscountPercent);
        var stored = await source.Set<OriginalOrder>().AsNoTracking().SingleAsync(row => row.Id == original.Id, token);
        Assert.Equal(original.Remaining, stored.Remaining);
        Assert.Equal(original.Subtotal, stored.Subtotal);
        Assert.Equal(original.Turnaround, stored.Turnaround);
        return original;
    }

    private static void AssertOriginal(string actual, OriginalOrder original, int generatedId)
    {
        var settings = new MvcNewtonsoftJsonOptions().SerializerSettings;
        settings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        settings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var expected = JsonNode.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(original, settings))!;
        // The independently inserted oracle and actual HTTP create receive different generated IDs.
        expected["id"] = generatedId;
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(actual)));
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, int excluded = -1)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().Where(row => row.Id != excluded).OrderBy(row => row.Id).ToArrayAsync(token),
            Processes = await orders.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Categories = await orders.Categories.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Files = await orders.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Deletions = await orders.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
        });
    }

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
            order.Property(row => row.Name).HasMaxLength(100).HasDefaultValue("unnamed");
            order.Property(row => row.Remaining).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.Subtotal).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.Turnaround).ValueGeneratedOnAddOrUpdate();
            order.Property(row => row.UnitPrice).HasPrecision(18, 2);
            order.Property(row => row.DiscountPercent).HasPrecision(5, 2);
            // Disposable PostgreSQL storage adapter; explicit original CLR UTC values stay untouched.
            foreach (var name in new[] { "CreatedDate", "ModifiedDate" })
                order.Property<DateTime?>(name).HasConversion(
                    value => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified) : value, value => value);
        }
    }
}
