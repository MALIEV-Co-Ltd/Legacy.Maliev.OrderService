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
using Npgsql;
using NpgsqlTypes;

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
        foreach (var field in new[] { "name", "extension" })
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
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"OrderId\":{},\"Bucket\":\"metadata-only\",\"ObjectName\":\"metadata-only\"}"));
        var request = JsonSerializer.Deserialize<UpsertFileFormatRequest>(Payload("12.3400E+03"))!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request));
        foreach (var field in new[] { "Name", "Extension" })
        {
            Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty(field).ValueKind);
            Assert.Equal("12.3400E+03", wire.RootElement.GetProperty(field).GetString());
        }
    }


    [Theory]
    [InlineData("Name", false, false)]
    [InlineData("Name", false, true)]
    [InlineData("Name", true, false)]
    [InlineData("Name", true, true)]
    [InlineData("Extension", false, false)]
    [InlineData("Extension", false, true)]
    [InlineData("Extension", true, false)]
    [InlineData("Extension", true, true)]
    public async Task NullableFileFormat_Utf16BoundaryPersistsAndOverflowDoesNotWrite(string field, bool update, bool supplementary)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        var original = update ? await CreateControlAsync(writer) : null;
        var exact = supplementary ? string.Concat(Enumerable.Repeat("\U0001F600", 25)) : new string('\u0e01', 50);
        Assert.Equal(50, exact.Length);
        foreach (var value in new string?[] { null, "", "  \u0e01 padded ", exact })
        {
            var before = await SnapshotAsync(app, original?.Id);
            clock.Advance(TimeSpan.FromSeconds(1));
            using var response = await SendAsync(writer, update, original?.Id ?? 0, FileFormatLengthPayload(field, value));
            Assert.Equal(update ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
            var id = original?.Id ?? (await response.Content.ReadFromJsonAsync<FileFormatResponse>())!.Id;
            if (!update) Assert.NotNull(response.Headers.Location);
            var current = (await reader.GetFromJsonAsync<FileFormatResponse>($"/orders/fileformats/{id}"))!;
            Assert.Equal(value, field == "Name" ? current.Name : current.Extension);
            Assert.Equal(field == "Name" ? ".old" : "Control", field == "Name" ? current.Extension : current.Name);
            if (original is not null) Assert.Equal(original.CreatedDate, current.CreatedDate);
            Assert.Equal(clock.GetUtcNow().UtcDateTime, current.ModifiedDate);
            await using (var scope = app.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
                var stored = await database.FileFormats.AsNoTracking().SingleAsync(row => row.Id == id);
                Assert.Equal(value, field == "Name" ? stored.Name : stored.Extension);
            }
            Assert.Equal(before, await SnapshotAsync(app, id));
        }
        var overflow = supplementary ? string.Concat(Enumerable.Repeat("\U0001F600", 26)) : new string('\u0e01', 51);
        Assert.True(overflow.Length > 50);
        var unchanged = await SnapshotAsync(app);
        using var invalid = await SendAsync(writer, update, original?.Id ?? 0, FileFormatLengthPayload(field, overflow));
        // Existing Process/Status HTTP 400 adaptation; original SQL behavior is
        // represented by the independent physical constraint tests below.
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(unchanged, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Extension")]
    public async Task FileFormatLength_AuthorityAndMissingUpdatePrecedeValidation(string field)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var original = await CreateControlAsync(writer);
        var before = await SnapshotAsync(app);
        var payload = FileFormatLengthPayload(field, string.Concat(Enumerable.Repeat("\U0001F600", 26)));
        using var anonymous = app.CreateClient();
        using var denied = Client(app, OrderPermissions.CatalogRead);
        foreach (var update in new[] { false, true })
        {
            using var unauthorized = await SendAsync(anonymous, update, original.Id, payload);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var forbidden = await SendAsync(denied, update, original.Id, payload);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
        using var missing = await SendAsync(writer, true, int.MaxValue, payload);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("Extension", false)]
    [InlineData("Extension", true)]
    public async Task DirectFileFormat_OverflowIsRefusedBeforeTrackedMutation(string field, bool update)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var original = await CreateControlAsync(writer);
        var before = await SnapshotAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var overflow = string.Concat(Enumerable.Repeat("\U0001F600", 26));
        var request = new UpsertFileFormatRequest(field == "Name" ? overflow : "Control", field == "Extension" ? overflow : ".old");
        if (update) await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateFileFormatAsync(original.Id, request, CancellationToken.None));
        else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFileFormatAsync(request, CancellationToken.None));
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<OrderDbContext>().ChangeTracker.Entries(),
            entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("Extension")]
    public async Task PhysicalFileFormat_NullAndFiftyUtf16UnitsPersistButTwentySixSupplementaryCharactersFail(string field)
    {
        await using var app = await AppAsync(new FakeTimeProvider());
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var original = await CreateControlAsync(writer);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        foreach (var update in new[] { false, true })
        {
            foreach (var value in new string?[] { null, "", new string('\u0e01', 50), string.Concat(Enumerable.Repeat("\U0001F600", 25)) })
            {
                var id = await WriteFileFormatLengthAsync(database, field, update, original.Id, value);
                var stored = await database.FileFormats.AsNoTracking().SingleAsync(row => row.Id == id);
                Assert.Equal(value, field == "Name" ? stored.Name : stored.Extension);
                Assert.Equal(field == "Name" ? ".old" : "Control", field == "Name" ? stored.Extension : stored.Name);
            }
            var before = await SnapshotAsync(app);
            var overflow = string.Concat(Enumerable.Repeat("\U0001F600", 26));
            Assert.Equal(52, overflow.Length);
            Assert.Equal(26, overflow.EnumerateRunes().Count());
            var failure = await Assert.ThrowsAsync<PostgresException>(() => WriteFileFormatLengthAsync(database, field, update, original.Id, overflow));
            Assert.Equal("23514", failure.SqlState);
            Assert.Equal("CK_FileFormat_" + field + "_SourceLength", failure.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(app));
            Assert.Null(database.Database.CurrentTransaction);
        }
    }

    private static string FileFormatLengthPayload(string field, string? value) => JsonSerializer.Serialize(
        new Dictionary<string, string?> { ["Name"] = field == "Name" ? value : "Control", ["Extension"] = field == "Extension" ? value : ".old" });

    private static async Task<int> WriteFileFormatLengthAsync(OrderDbContext database, string field, bool update, int id, string? value)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await database.Database.OpenConnectionAsync(lifetime.Token);
        try
        {
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = (field, update) switch
            {
                ("Name", false) => """INSERT INTO public."FileFormat" ("Name","Extension") VALUES (@value,'.old') RETURNING "ID";""",
                ("Name", true) => """UPDATE public."FileFormat" SET "Name"=@value WHERE "ID"=@id RETURNING "ID";""",
                ("Extension", false) => """INSERT INTO public."FileFormat" ("Name","Extension") VALUES ('Control',@value) RETURNING "ID";""",
                ("Extension", true) => """UPDATE public."FileFormat" SET "Extension"=@value WHERE "ID"=@id RETURNING "ID";""",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = -1 });
            if (update) command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            var result = await command.ExecuteScalarAsync(lifetime.Token);
            return Assert.IsType<int>(result);
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
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
