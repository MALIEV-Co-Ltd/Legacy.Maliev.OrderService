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

public sealed class OrderFileLiteralHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const string SentinelKey = "legacy:order:file-literal:sentinel";

    [Theory]
    [InlineData("  metadata  ", "  folder/ไทย.step  ")]
    [InlineData("metadata", "  drawing.step  ")]
    [InlineData("  metadata", "drawing.step  ")]
    public async Task QueryCreate_LiteralRetryPreservesStorageLocationAndUnrelatedGraph(string bucket, string objectName)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var before = await SnapshotAsync(app);
        var first = await CreateAsync(writer, bucket, objectName);
        var retry = await CreateAsync(writer, bucket, objectName);
        Assert.Equal(first, retry);
        await AssertReadbackAsync(app, reader, first, bucket, objectName);
        Assert.Equal(before, await SnapshotAsync(app, first.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueryCreate_LiteralsThatOnlyDifferByEdgeWhitespaceHaveSeparateExactRetryIdentities(bool bucketDiffers)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var before = await SnapshotAsync(app);
        var first = await CreateAsync(writer, "metadata", "drawing.step");
        var bucket = bucketDiffers ? " metadata " : "metadata";
        var objectName = bucketDiffers ? "drawing.step" : " drawing.step ";
        var literal = await CreateAsync(writer, bucket, objectName);
        Assert.NotEqual(first.Id, literal.Id);
        Assert.Equal(first, await CreateAsync(writer, "metadata", "drawing.step"));
        Assert.Equal(literal, await CreateAsync(writer, bucket, objectName));
        await AssertReadbackAsync(app, reader, first, "metadata", "drawing.step");
        await AssertReadbackAsync(app, reader, literal, bucket, objectName);
        Assert.Equal(before, await SnapshotAsync(app, first.Id, literal.Id));
    }

    [Fact]
    public async Task ConcurrentExactLiteralRetriesReturnOneRecordWithoutTouchingRetainedTrimmedIdentity()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var retained = await CreateAsync(writer, "metadata", "drawing.step");
        var before = await SnapshotAsync(app);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateAsync(writer, " metadata ", " drawing.step ")));
        var literal = results[0];
        Assert.All(results, value => Assert.Equal(literal, value));
        Assert.NotEqual(retained.Id, literal.Id);
        await AssertReadbackAsync(app, reader, literal, " metadata ", " drawing.step ");
        await AssertReadbackAsync(app, reader, retained, "metadata", "drawing.step");
        Assert.Equal(before, await SnapshotAsync(app, literal.Id));
    }

    [Theory]
    [InlineData(true, "123", "123")]
    [InlineData(false, "123", "123")]
    [InlineData(true, "-0", "-0")]
    [InlineData(false, "-0", "-0")]
    [InlineData(true, "12.3400E+03", "12.3400E+03")]
    [InlineData(false, "12.3400E+03", "12.3400E+03")]
    [InlineData(true, "true", "true")]
    [InlineData(false, "true", "true")]
    [InlineData(true, "false", "false")]
    [InlineData(false, "false", "false")]
    [InlineData(true, "\"  ไทย  \"", "  ไทย  ")]
    [InlineData(false, "\"  ไทย  \"", "  ไทย  ")]
    [InlineData(true, "\"   \"", "   ")]
    [InlineData(false, "\"   \"", "   ")]
    public async Task JsonPut_StandardPrimitiveOrLiteralPreservesExactTextAndOwner(bool bucketField, string token, string expected)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero));
        await using var app = await AppAsync(clock);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var original = await CreateAsync(writer, "control", "control.step");
        var before = await SnapshotAsync(app, original.Id);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var response = await PutAsync(writer, original.Id, Payload(bucketField, token));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var current = (await reader.GetFromJsonAsync<OrderFileResponse>($"/orders/files/{original.Id}"))!;
        Assert.Equal(original.OrderId, current.OrderId);
        Assert.Equal(original.CreatedDate, current.CreatedDate);
        Assert.True(current.ModifiedDate > original.ModifiedDate);
        await AssertReadbackAsync(app, reader, current, bucketField ? expected : " control ", bucketField ? " control.step " : expected);
        Assert.Equal(before, await SnapshotAsync(app, original.Id));
    }

    [Theory]
    [InlineData(true, "[]")]
    [InlineData(false, "[]")]
    [InlineData(true, "{}")]
    [InlineData(false, "{}")]
    [InlineData(true, "null")]
    [InlineData(false, "null")]
    public async Task JsonPut_NestedOrRequiredNullRefusesWithoutMutatingAnyGraph(bool bucketField, string token)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        var original = await CreateAsync(writer, "control", "control.step");
        var before = await SnapshotAsync(app);
        using var response = await PutAsync(writer, original.Id, Payload(bucketField, token));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task QueryOrJsonWrite_AnonymousOrReadOnlyCannotMutateGraph(bool update, bool anonymous)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        var original = await CreateAsync(writer, "control", "control.step");
        using var client = anonymous ? app.CreateClient() : Client(app, OrderPermissions.FilesRead);
        var before = await SnapshotAsync(app);
        using var response = update ? await PutAsync(client, original.Id, Payload(true, "123")) : await client.PostAsync(Query(9003, " metadata ", " drawing.step "), null);
        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData("bucket= &objectName=drawing.step")]
    [InlineData("bucket=metadata&objectName= ")]
    [InlineData("bucket=&objectName=drawing.step")]
    [InlineData("objectName=drawing.step")]
    [InlineData("bucket=metadata")]
    public async Task OriginalQueryWhitespaceEmptyOrMissingStillRefusesWithoutWrites(string query)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        var before = await SnapshotAsync(app);
        using var response = await writer.PostAsync($"/orders/9003/files?{query.Replace(" ", "%20", StringComparison.Ordinal)}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task RepeatedQueryKeysPreserveOriginalFirstValueWithoutTrimming()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var before = await SnapshotAsync(app);
        using var response = await writer.PostAsync("/orders/9003/files?bucket=%20metadata%20&bucket=ignored&objectName=%20drawing.step%20&objectName=ignored", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var file = (await response.Content.ReadFromJsonAsync<OrderFileResponse>())!;
        await AssertReadbackAsync(app, reader, file, " metadata ", " drawing.step ");
        Assert.Equal(before, await SnapshotAsync(app, file.Id));
    }

    [Fact]
    public void FileConvertersRemainPropertyScopedWithUntouchedOwnerIntegerAndStringSerialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"OrderId\":{},\"Bucket\":\"metadata\",\"ObjectName\":\"drawing.step\"}"));
        var request = JsonSerializer.Deserialize<UpsertOrderFileRequest>("{\"Bucket\":12.3400E+03,\"ObjectName\":true}")!;
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("Bucket").ValueKind);
        Assert.Equal("12.3400E+03", wire.RootElement.GetProperty("Bucket").GetString());
        Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("ObjectName").ValueKind);
        Assert.Equal("true", wire.RootElement.GetProperty("ObjectName").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOwnerOrFileDoesNotWriteLiteralMetadata(bool update)
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        var before = await SnapshotAsync(app);
        using var response = update ? await PutAsync(writer, 999999, Payload(true, "123")) : await writer.PostAsync(Query(999999, " metadata ", " drawing.step "), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Fact]
    public async Task JsonPut_ReparentKeepsExactMetadataAndBothOrdersOtherGraphs()
    {
        await using var app = await AppAsync();
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        var original = await CreateAsync(writer, "control", "control.step");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            orders.Orders.Add(new Order { Id = 9010, ProcessId = 9002, Name = "Other metadata owner", Quantity = 1 });
            await orders.SaveChangesAsync();
        }
        var before = await SnapshotAsync(app, original.Id);
        using var response = await PutAsync(writer, original.Id, "{\"OrderId\":9010,\"Bucket\":\" metadata \",\"ObjectName\":\" drawing.step \"}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var current = (await reader.GetFromJsonAsync<OrderFileResponse>($"/orders/files/{original.Id}"))!;
        Assert.Equal(9010, current.OrderId);
        Assert.Equal(original.CreatedDate, current.CreatedDate);
        await AssertReadbackAsync(app, reader, current, " metadata ", " drawing.step ");
        using var oldOwnerResponse = await reader.GetAsync("/orders/9003/files");
        Assert.Equal(HttpStatusCode.NotFound, oldOwnerResponse.StatusCode);
        await using (var oldOwnerScope = app.Services.CreateAsyncScope())
        {
            Assert.False(await oldOwnerScope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().AnyAsync(value => value.OrderId == 9003));
        }
        Assert.Equal(before, await SnapshotAsync(app, original.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedLifetimeCannotBeResurrectedByLiteralQueryOrPrimitivePut(bool update)
    {
        await using var app = await AppAsync(recoveryEnabled: true);
        await SeedAsync(app);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var deleter = Client(app, OrderPermissions.Delete);
        var original = await CreateAsync(writer, "control", "control.step");
        using var deleted = await deleter.DeleteAsync("/orders/9003");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var before = await SnapshotAsync(app);
        using var response = update ? await PutAsync(writer, original.Id, Payload(true, "123")) : await writer.PostAsync(Query(9003, " metadata ", " drawing.step "), null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    private Task<WebApplicationFactory<Program>> AppAsync(FakeTimeProvider? clock = null, bool recoveryEnabled = false) => fixture.AppAsync(null, recoveryEnabled: recoveryEnabled, configure: services =>
    {
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock ?? new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero)));
    });

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static string Query(int orderId, string bucket, string objectName) => $"/orders/{orderId}/files?bucket={Uri.EscapeDataString(bucket)}&objectName={Uri.EscapeDataString(objectName)}";
    private static string Payload(bool bucketField, string token) => bucketField ? $"{{\"Bucket\":{token},\"ObjectName\":\" control.step \"}}" : $"{{\"Bucket\":\" control \",\"ObjectName\":{token}}}";

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, int id, string payload)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await client.PutAsync($"/orders/files/{id}", content);
    }

    private static async Task<OrderFileResponse> CreateAsync(HttpClient client, string bucket, string objectName)
    {
        using var response = await client.PostAsync(Query(9003, bucket, objectName), null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        var file = (await response.Content.ReadFromJsonAsync<OrderFileResponse>())!;
        Assert.Equal($"/orders/Files/{file.Id}", response.Headers.Location!.AbsolutePath);
        return file;
    }

    private static async Task AssertReadbackAsync(WebApplicationFactory<Program> app, HttpClient reader, OrderFileResponse file, string bucket, string objectName)
    {
        using var fresh = await reader.GetAsync($"/orders/files/{file.Id}");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using var wire = JsonDocument.Parse(await fresh.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.String, wire.RootElement.GetProperty("Bucket").ValueKind);
        Assert.Equal(bucket, wire.RootElement.GetProperty("Bucket").GetString());
        Assert.Equal(objectName, wire.RootElement.GetProperty("ObjectName").GetString());
        Assert.Equal(file, await reader.GetFromJsonAsync<OrderFileResponse>($"/orders/files/{file.Id}"));
        Assert.Contains(file, (await reader.GetFromJsonAsync<OrderFileResponse[]>($"/orders/{file.OrderId}/files"))!);
        await using var scope = app.Services.CreateAsyncScope();
        var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().SingleAsync(value => value.Id == file.Id);
        Assert.Equal(bucket, row.Bucket);
        Assert.Equal(objectName, row.ObjectName);
        Assert.Equal(file, new OrderFileResponse(row.Id, row.OrderId, row.Bucket, row.ObjectName, row.CreatedDate, row.ModifiedDate));
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        orders.Categories.Add(new Category { Id = 9001, Name = "Unrelated category" });
        orders.Processes.Add(new Process { Id = 9002, CategoryId = 9001, Name = "Unrelated process" });
        orders.Orders.AddRange(new Order { Id = 9003, ProcessId = 9002, Name = "Metadata owner", Quantity = 1 }, new Order { Id = 9011, ProcessId = 9002, Name = "Unrelated metadata owner", Quantity = 1 });
        orders.FileFormats.Add(new FileFormat { Id = 9004, Name = "Unrelated format", Extension = ".sentinel" });
        orders.Files.Add(new OrderFile { Id = 9005, OrderId = 9011, Bucket = "sentinel", ObjectName = "unrelated.step" });
        await orders.SaveChangesAsync();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        statuses.Statuses.AddRange(new OrderStatus { Id = 9006, Name = "Unrelated status" }, new OrderStatus { Id = 9007, Name = "Unrelated next status" });
        statuses.Transitions.Add(new OrderStatusTransition { Id = 9008, OrderStatusId = 9006, PossibleStatusId = 9007 });
        statuses.History.Add(new OrderStatusHistory { Id = 9009, OrderId = 9011, OrderStatusId = 9006 });
        await statuses.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<IDistributedCache>().SetStringAsync(SentinelKey, "unchanged", new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5) });
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, params int[] excludedFileIds)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Processes = await orders.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Categories = await orders.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Formats = await orders.FileFormats.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Files = await orders.Files.AsNoTracking().Where(value => !excludedFileIds.Contains(value.Id)).OrderBy(value => value.Id).ToArrayAsync(),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            History = await statuses.History.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(),
            Cache = await scope.ServiceProvider.GetRequiredService<IDistributedCache>().GetStringAsync(SentinelKey)
        });
    }
}
