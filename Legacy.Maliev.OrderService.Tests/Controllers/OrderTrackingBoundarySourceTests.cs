using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Npgsql;
using NpgsqlTypes;
using Xunit.Abstractions;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderTrackingBoundarySourceTests(OrderDeletionReadinessFixture fixture, ITestOutputHelper output)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private static int nextIdentity = 7_000_000;
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("http", false)]
    [InlineData("http", true)]
    [InlineData("repository", false)]
    [InlineData("repository", true)]
    [InlineData("sql", false)]
    [InlineData("sql", true)]
    public async Task QualifiedExcessAsciiPaddingRetainsFreshHttpLiteralAndBoundedStoredValue(string boundary, bool update)
    {
        // DataMigration263 separately records current-source EF8.0.28 parameter and saved-value behavior.
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = lifetime.Token;
        var (process, existing) = await SeedAsync(app, token);
        var input = new string('A', 250) + " ";
        var expectedStored = new string('A', 250);
        Assert.Equal(251, input.Length);
        var otherRows = await SnapshotAsync(app, token, update ? [existing] : []);
        var otherFields = await OtherTargetFieldsAsync(app, existing, boundary != "sql", token);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var id = existing;
        string result;
        if (boundary == "http")
        {
            using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
            using var response = await SendAsync(writer, update, existing, Request(process, input), token);
            result = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            output.WriteLine($"Current Legacy HTTP inputUtf16=251 status={result}");
            Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync(token);
            if (update) Assert.Empty(raw);
            else
            {
                AssertWireTracking(raw, input);
                id = JsonSerializer.Deserialize<OrderResponse>(raw, WebJson)!.Id;
            }
        }
        else if (boundary == "repository")
        {
            if (update)
            {
                var changed = await service.UpdateOrderAsync(existing, Request(process, input), null, token);
                result = changed.ToString();
                Assert.Equal(UpdateResult.Updated, changed);
            }
            else
            {
                var created = await service.CreateOrderAsync(Request(process, input), token);
                id = created.Id;
                result = "Created";
                Assert.Equal(expectedStored, created.TrackingNumber);
            }
        }
        else
        {
            Assert.Equal("sql", boundary);
            id = await WritePhysicalAsync(scope.ServiceProvider.GetRequiredService<OrderDbContext>(), update, existing, process, input, token);
            result = "Written";
        }
        var stored = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking()
            .SingleAsync(value => value.Id == id, token);
        output.WriteLine($"Current Legacy boundary={boundary} update={update} result={result} inputUtf16={input.Length} storedUtf16={stored.TrackingNumber?.Length}");
        Assert.Equal(expectedStored, stored.TrackingNumber);
        Assert.Equal(otherRows, await SnapshotAsync(app, token, [id]));
        if (update) Assert.Equal(otherFields, await OtherTargetFieldsAsync(app, existing, boundary != "sql", token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpWritesRetainNullableLiteralTrackingAndRejectUtf16OverflowWithoutMutation(bool update)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = lifetime.Token;
        var (process, existing) = await SeedAsync(app, token);
        using var writer = Client(app, update ? OrderPermissions.Update : OrderPermissions.Create);
        using var reader = Client(app, OrderPermissions.Read);
        List<int> writtenIds = update ? [existing] : [];
        var originalTarget = await OtherTargetFieldsAsync(app, existing, true, token);
        foreach (var literal in Accepted())
        {
            var otherRows = await SnapshotAsync(app, token, writtenIds);
            using var response = await SendAsync(writer, update, existing, Request(process, literal), token);
            Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync(token);
            int id;
            if (update)
            {
                Assert.Empty(raw);
                id = existing;
            }
            else
            {
                AssertWireTracking(raw, literal);
                var created = JsonSerializer.Deserialize<OrderResponse>(raw, WebJson)!;
                Assert.Equal(literal, created.TrackingNumber);
                id = created.Id;
                writtenIds.Add(id);
                Assert.NotNull(response.Headers.Location);
            }
            using var read = await reader.GetAsync($"/orders/{id}", token);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            var readRaw = await read.Content.ReadAsStringAsync(token);
            AssertWireTracking(readRaw, Stored(literal));
            var actual = JsonSerializer.Deserialize<OrderResponse>(readRaw, WebJson);
            Assert.NotNull(actual);
            Assert.Equal(Stored(literal), actual.TrackingNumber);
            await AssertStoredAsync(app, id, Stored(literal), token);
            if (update) Assert.Equal(originalTarget, await OtherTargetFieldsAsync(app, existing, true, token));
            Assert.Equal(otherRows, await SnapshotAsync(app, token, writtenIds));
        }

        var unchanged = await SnapshotAsync(app, token);
        foreach (var overflow in Rejected())
        {
            using var response = await SendAsync(writer, update, existing, Request(process, overflow), token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(token);
            Assert.NotNull(problem);
            Assert.Contains(nameof(UpsertOrderRequest.TrackingNumber), problem.Errors.Keys);
            Assert.Equal(unchanged, await SnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectWritesEnforceTrackingBoundsAndRetainReplayLookupAndConcurrencyPriority(bool update)
    {
        await using var app = await fixture.AppAsync(null, recoveryEnabled: true);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = lifetime.Token;
        var (process, existing) = await SeedAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        List<int> writtenIds = update ? [existing] : [];
        var originalTarget = await OtherTargetFieldsAsync(app, existing, true, token);
        foreach (var literal in Accepted())
        {
            var otherRows = await SnapshotAsync(app, token, writtenIds);
            int id;
            if (update)
            {
                Assert.Equal(UpdateResult.Updated, await service.UpdateOrderAsync(existing, Request(process, literal), null, token));
                id = existing;
            }
            else
            {
                var created = await service.CreateOrderAsync(Request(process, literal), token);
                Assert.Equal(Stored(literal), created.TrackingNumber);
                Assert.NotNull(created.SourceCreation);
                Assert.Equal(literal, created.SourceCreation.TrackingNumber);
                var read = await service.GetOrderAsync(created.Id, token);
                Assert.NotNull(read);
                Assert.Equal(created, read);
                Assert.Equal(created.GetHashCode(), read.GetHashCode());
                Assert.Equal(JsonSerializer.Serialize(created), JsonSerializer.Serialize(read));
                id = created.Id;
                writtenIds.Add(id);
            }
            await AssertStoredAsync(app, id, Stored(literal), token);
            if (update) Assert.Equal(originalTarget, await OtherTargetFieldsAsync(app, existing, true, token));
            Assert.Equal(otherRows, await SnapshotAsync(app, token, writtenIds));
        }

        var invalid = Request(process, Rejected()[0]);
        if (update)
        {
            var unchanged = await SnapshotAsync(app, token);
            Assert.Equal(UpdateResult.NotFound, await service.UpdateOrderAsync(int.MaxValue, invalid, null, token));
            Assert.Equal(UpdateResult.Conflict, await service.UpdateOrderAsync(existing, Request(process, "valid"),
                new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), token));
            Assert.Equal(unchanged, await SnapshotAsync(app, token));
            var deleted = await service.CreateOrderAsync(Request(process, "deleted control"), token);
            Assert.True(await service.DeleteOrderAsync(deleted.Id, token));
            unchanged = await SnapshotAsync(app, token);
            Assert.Equal(UpdateResult.NotFound, await service.UpdateOrderAsync(deleted.Id, invalid, null, token));
            Assert.Equal(unchanged, await SnapshotAsync(app, token));
        }
        else
        {
            var operationKey = "tracking-" + Guid.NewGuid().ToString("N");
            var retained = await service.CreateOrderAsync(Request(process, "durable control") with { OperationKey = operationKey }, token);
            var unchanged = await SnapshotAsync(app, token);
            Assert.Equal(retained, await service.CreateOrderAsync(invalid with { OperationKey = operationKey }, token));
            Assert.Equal(unchanged, await SnapshotAsync(app, token));
        }
        var beforeRefusal = await SnapshotAsync(app, token);
        foreach (var overflow in Rejected())
        {
            if (update)
                await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateOrderAsync(existing, Request(process, overflow), null, token));
            else
                await Assert.ThrowsAsync<ArgumentException>(() => service.CreateOrderAsync(Request(process, overflow), token));
            Assert.Equal(beforeRefusal, await SnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalWritesEnforceNullableUtf16TrackingWithoutTrimmingOrOtherRowChanges(bool update)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = lifetime.Token;
        var (process, existing) = await SeedAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        List<int> writtenIds = update ? [existing] : [];
        var originalTarget = await OtherTargetFieldsAsync(app, existing, false, token);
        foreach (var literal in Accepted())
        {
            var otherRows = await SnapshotAsync(app, token, writtenIds);
            var id = await WritePhysicalAsync(database, update, existing, process, literal, token);
            if (!writtenIds.Contains(id)) writtenIds.Add(id);
            await AssertStoredAsync(app, id, Stored(literal), token);
            if (update) Assert.Equal(originalTarget, await OtherTargetFieldsAsync(app, existing, false, token));
            Assert.Equal(otherRows, await SnapshotAsync(app, token, writtenIds));
        }
        foreach (var overflow in Rejected())
        {
            var unchanged = await SnapshotAsync(app, token);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => WritePhysicalAsync(database, update, existing, process, overflow, token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("CK_Order_TrackingNumber_SourceLength", failure.ConstraintName);
            Assert.Equal(unchanged, await SnapshotAsync(app, token));
            Assert.Null(database.Database.CurrentTransaction);
            Assert.Equal(System.Data.ConnectionState.Closed, database.Database.GetDbConnection().State);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TrackingWritesRequireTheExactWritePermission(bool update, bool anonymous)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var (process, existing) = await SeedAsync(app, lifetime.Token);
        using var client = Client(app, anonymous ? null : OrderPermissions.Read);
        var unchanged = await SnapshotAsync(app, lifetime.Token);
        using var response = await SendAsync(client, update, existing, Request(process, "  ไทย 😀  "), lifetime.Token);
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(unchanged, await SnapshotAsync(app, lifetime.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControllerRefusesTrackingOverflowBeforeServiceAndIdempotencyCalls(bool update)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var cache = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var controller = new OrdersController(service.Object, cache.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "tracking-control")], "test")),
                },
            },
        };
        foreach (var overflow in Rejected())
        {
            var result = update
                ? await controller.UpdateOrderAsync(42, Request(1, overflow), null, CancellationToken.None)
                : await controller.CreateOrderAsync(Request(1, overflow), "tracking-control", CancellationToken.None);
            var bad = Assert.IsType<BadRequestObjectResult>(result);
            Assert.Contains(nameof(UpsertOrderRequest.TrackingNumber), Assert.IsType<ValidationProblemDetails>(bad.Value).Errors.Keys);
        }
        service.VerifyNoOtherCalls();
        cache.VerifyNoOtherCalls();
    }

    [Fact]
    public void TrackingValidationKeepsNullableLiteralUtf16SourceLimit()
    {
        var parameter = typeof(UpsertOrderRequest).GetConstructors().Single().GetParameters()
            .Single(value => value.Name == nameof(UpsertOrderRequest.TrackingNumber));
        var bound = Assert.Single(parameter.GetCustomAttributes(typeof(OrderTrackingNumberSourceLengthAttribute), false).Cast<OrderTrackingNumberSourceLengthAttribute>());
        Assert.Equal(250, OrderTrackingNumberSourceLengthAttribute.MaximumLength);
        foreach (var value in Accepted()) Assert.True(bound.IsValid(value));
        foreach (var value in Rejected()) Assert.False(bound.IsValid(value));
    }

    [Fact]
    public async Task FreshTrackingLiteralIsCachedWhileDurableReplayReturnsStoredValueWithoutMutation()
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = lifetime.Token;
        var (process, _) = await SeedAsync(app, token);
        var literal = string.Concat(Enumerable.Repeat("😀", 124)) + "   ";
        var request = Request(process, literal) with { OperationKey = Guid.NewGuid().ToString("N") };
        using var cached = Client(app, OrderPermissions.Create);
        cached.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        using var first = await cached.PostAsJsonAsync("/orders", request, token);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var raw = await first.Content.ReadAsStringAsync(token);
        AssertWireTracking(raw, literal);
        var id = JsonSerializer.Deserialize<OrderResponse>(raw, WebJson)!.Id;
        await AssertStoredAsync(app, id, Stored(literal), token);
        var unchanged = await SnapshotAsync(app, token);
        using var replay = await cached.PostAsJsonAsync("/orders", request, token);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(raw, await replay.Content.ReadAsStringAsync(token));
        Assert.Equal(unchanged, await SnapshotAsync(app, token));
        using var durable = Client(app, OrderPermissions.Create);
        using var durableReplay = await durable.PostAsJsonAsync("/orders", request with { TrackingNumber = "Different replay input" }, token);
        Assert.Equal(HttpStatusCode.Created, durableReplay.StatusCode);
        var durableRaw = await durableReplay.Content.ReadAsStringAsync(token);
        AssertWireTracking(durableRaw, Stored(literal));
        Assert.Equal(id, JsonSerializer.Deserialize<OrderResponse>(durableRaw, WebJson)!.Id);
        Assert.Equal(unchanged, await SnapshotAsync(app, token));
    }

    private static string?[] Accepted() =>
        [null, "", "  ไทย 😀  ", new string('ก', 250), string.Concat(Enumerable.Repeat("😀", 125)),
            new string('ก', 248) + "  ", string.Concat(Enumerable.Repeat("😀", 124)) + "  ",
            new string('A', 250) + " ", string.Concat(Enumerable.Repeat("😀", 125)) + " ",
            string.Concat(Enumerable.Repeat("😀", 124)) + "   "];

    private static string[] Rejected() => [string.Concat(Enumerable.Repeat("😀", 126)), new string('ก', 251),
        new string('A', 250) + "\u00a0", new string('A', 250) + "\t", new string('A', 249) + "😀 "];

    private static string? Stored(string? input) => input is { Length: > 250 } ? input[..250] : input;

    private static void AssertWireTracking(string raw, string? tracking)
    {
        using var document = JsonDocument.Parse(raw);
        Assert.True(document.RootElement.TryGetProperty("id", out _));
        Assert.False(document.RootElement.TryGetProperty("TrackingNumber", out _));
        if (tracking is null) Assert.False(document.RootElement.TryGetProperty("trackingNumber", out _));
        else Assert.Equal(tracking, document.RootElement.GetProperty("trackingNumber").GetString());
    }

    private static UpsertOrderRequest Request(int process, string? tracking) =>
        JsonSerializer.Deserialize<UpsertOrderRequest>(JsonSerializer.Serialize(new
        {
            Name = "Tracking target",
            ProcessId = process,
            Quantity = 1,
            TrackingNumber = tracking,
        }))!;

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool update, int id, UpsertOrderRequest request, CancellationToken token)
    {
        using var message = new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Post, update ? $"/orders/{id}" : "/orders")
        {
            Content = JsonContent.Create(request),
        };
        return await client.SendAsync(message, token);
    }

    private static async Task<(int Process, int Order)> SeedAsync(WebApplicationFactory<Program> app, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var firstId = Interlocked.Add(ref nextIdentity, 100);
        var category = new Category { Name = "Tracking control" };
        var process = new Process { Name = "Tracking control", Category = category };
        orders.Processes.Add(process);
        await orders.SaveChangesAsync(token);
        orders.Orders.AddRange(new Order { Id = firstId, ProcessId = process.Id, Name = "Tracking target", Quantity = 1 },
            new Order { Id = firstId + 1, ProcessId = process.Id, Name = "Retained sibling", Quantity = 3 });
        orders.FileFormats.Add(new FileFormat { Name = "Retained format", Extension = ".step" });
        orders.Files.Add(new OrderFile { OrderId = firstId + 1, Bucket = "tracking-control", ObjectName = "retained.step" });
        await orders.SaveChangesAsync(token);
        await orders.Database.ExecuteSqlInterpolatedAsync($"SELECT setval(pg_get_serial_sequence('public.\"Order\"', 'ID'), {firstId + 10}, false)", token);
        var status = new OrderStatus { Name = "Tracking control" };
        var next = new OrderStatus { Name = "Tracking next" };
        statuses.Statuses.AddRange(status, next);
        await statuses.SaveChangesAsync(token);
        statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = status.Id, PossibleStatusId = next.Id });
        statuses.History.Add(new OrderStatusHistory { OrderId = firstId + 1, OrderStatusId = status.Id });
        await statuses.SaveChangesAsync(token);
        return (process.Id, firstId);
    }

    private static async Task AssertStoredAsync(WebApplicationFactory<Program> app, int id, string? tracking, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id, token);
        Assert.Equal(tracking, row.TrackingNumber);
    }

    private static async Task<string> OtherTargetFieldsAsync(WebApplicationFactory<Program> app, int id, bool ignoreModified, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync(value => value.Id == id, token);
        return JsonSerializer.Serialize(JsonSerializer.SerializeToElement(row).EnumerateObject()
            .Where(value => value.Name != nameof(Order.TrackingNumber) && (!ignoreModified || value.Name != nameof(Order.ModifiedDate)))
            .ToDictionary(value => value.Name, value => value.Value));
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, List<int>? excludedIds = null)
    {
        excludedIds ??= [];
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().Where(value => !excludedIds.Contains(value.Id)).OrderBy(value => value.Id).ToArrayAsync(token),
            Categories = await orders.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Processes = await orders.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Files = await orders.Files.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
        });
    }

    private static async Task<int> WritePhysicalAsync(OrderDbContext database, bool update, int id, int process, string? tracking, CancellationToken token)
    {
        await database.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = update
                ? """UPDATE public."Order" SET "TrackingNumber"=@tracking WHERE "ID"=@id RETURNING "ID";"""
                : """INSERT INTO public."Order" ("ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment","TrackingNumber") VALUES (@process,1,0,false,false,false,@tracking) RETURNING "ID";""";
            command.Parameters.Add(new NpgsqlParameter("tracking", NpgsqlDbType.Text) { Value = (object?)tracking ?? DBNull.Value, Size = -1 });
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("process", NpgsqlDbType.Integer) { Value = process });
            return Assert.IsType<int>(await command.ExecuteScalarAsync(token));
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
