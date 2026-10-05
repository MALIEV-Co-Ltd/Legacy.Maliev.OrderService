using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileMetadataLifecycleHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Fact]
    public async Task MetadataLifecycle_ResolvesLocation_ReassignsParentAndPreservesFieldsAndTimestamps()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var first = await SeedOrderAsync(app, "First metadata parent");
        var second = await SeedOrderAsync(app, "Second metadata parent");
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        using var deleter = Client(app, OrderPermissions.FilesDelete);
        using var empty = await reader.GetAsync($"/orders/{first}/files");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await writer.PostAsync($"/orders/{first}/files?bucket=metadata-bucket&objectName=orders%2Fpart%20drawing.step", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var file = (await created.Content.ReadFromJsonAsync<OrderFileResponse>())!;
        Assert.Equal(first, file.OrderId);
        Assert.Equal("metadata-bucket", file.Bucket);
        Assert.Equal("orders/part drawing.step", file.ObjectName);
        Assert.NotNull(file.CreatedDate);
        Assert.NotNull(file.ModifiedDate);
        Assert.NotNull(created.Headers.Location);
        using var location = await reader.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, location.StatusCode);
        Assert.Equal(file, await location.Content.ReadFromJsonAsync<OrderFileResponse>());
        Assert.Equal(file, Assert.Single((await reader.GetFromJsonAsync<OrderFileResponse[]>($"/orders/{first}/files"))!));
        using var unrelated = await reader.GetAsync($"/orders/{second}/files");
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var updated = await writer.PutAsJsonAsync($"/orders/files/{file.Id}", new UpsertOrderFileRequest(second, "updated-bucket", "orders/revision.step"));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var current = (await reader.GetFromJsonAsync<OrderFileResponse>($"/orders/files/{file.Id}"))!;
        Assert.Equal(file.Id, current.Id);
        Assert.Equal(second, current.OrderId);
        Assert.Equal("updated-bucket", current.Bucket);
        Assert.Equal("orders/revision.step", current.ObjectName);
        Assert.Equal(file.CreatedDate, current.CreatedDate);
        Assert.True(current.ModifiedDate > file.ModifiedDate);
        using var oldParent = await reader.GetAsync($"/orders/{first}/files");
        Assert.Equal(HttpStatusCode.NotFound, oldParent.StatusCode);
        Assert.Equal(current, Assert.Single((await reader.GetFromJsonAsync<OrderFileResponse[]>($"/orders/{second}/files"))!));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var row = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().SingleAsync();
            Assert.Equal(current, new OrderFileResponse(row.Id, row.OrderId, row.Bucket, row.ObjectName, row.CreatedDate, row.ModifiedDate));
        }
        using var deleted = await deleter.DeleteAsync($"/orders/files/{file.Id}");
        using var missing = await reader.GetAsync($"/orders/files/{file.Id}");
        using var replay = await deleter.DeleteAsync($"/orders/files/{file.Id}");
        using var missingUpdate = await writer.PutAsJsonAsync($"/orders/files/{file.Id}", new UpsertOrderFileRequest(second, "missing", "missing.step"));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
        await using var finalScope = app.Services.CreateAsyncScope();
        var database = finalScope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Empty(await database.Files.ToArrayAsync());
        Assert.Equal(2, await database.Orders.CountAsync());
    }

    [Fact]
    public async Task MissingRecords_ReturnNotFoundWithoutCreatingOrphanMetadata()
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.FilesWrite);
        using var reader = Client(app, OrderPermissions.FilesRead);
        using var deleter = Client(app, OrderPermissions.FilesDelete);
        using var create = await writer.PostAsync("/orders/999999/files?bucket=metadata&objectName=missing.step", null);
        using var read = await reader.GetAsync("/orders/files/999999");
        using var list = await reader.GetAsync("/orders/999999/files");
        using var update = await writer.PutAsJsonAsync("/orders/files/999999", new UpsertOrderFileRequest(999999, "metadata", "missing.step"));
        using var delete = await deleter.DeleteAsync("/orders/files/999999");
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.ToArrayAsync());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("list")]
    [InlineData("update")]
    [InlineData("write")]
    [InlineData("delete")]
    public async Task MetadataRoutes_UnrelatedOrderGrant_IsForbiddenBeforeMutation(string operation)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, OrderPermissions.Read);
        using var response = operation switch
        {
            "read" => await client.GetAsync("/orders/files/999999"),
            "list" => await client.GetAsync("/orders/999999/files"),
            "update" => await client.PutAsJsonAsync("/orders/files/999999", new UpsertOrderFileRequest(null, "metadata", "missing.step")),
            "write" => await client.PostAsync("/orders/999999/files?bucket=metadata&objectName=missing.step", null),
            _ => await client.DeleteAsync("/orders/files/999999"),
        };
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.ToArrayAsync());
    }

    [Theory]
    [InlineData("", "drawing.step")]
    [InlineData("metadata", "")]
    [InlineData(" ", "drawing.step")]
    public async Task MetadataCreate_BlankQuery_RejectsWithoutPersisting(string bucket, string objectName)
    {
        await using var app = await fixture.AppAsync(null);
        var order = await SeedOrderAsync(app, "Invalid metadata parent");
        using var client = Client(app, OrderPermissions.FilesWrite);
        using var response = await client.PostAsync($"/orders/{order}/files?bucket={Uri.EscapeDataString(bucket)}&objectName={Uri.EscapeDataString(objectName)}", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.ToArrayAsync());
    }

    [Fact]
    public async Task MetadataCreate_RepeatedObject_CurrentIdempotencyPolicyReturnsSingleRecord()
    {
        // Keep exact record equality at the database's microsecond precision rather
        // than introducing a difference between fresh and persisted clock ticks.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var order = await SeedOrderAsync(app, "Repeated object parent");
        using var client = Client(app, OrderPermissions.FilesWrite);
        using var first = await client.PostAsync($"/orders/{order}/files?bucket=metadata&objectName=same.step", null);
        using var second = await client.PostAsync($"/orders/{order}/files?bucket=metadata&objectName=same.step", null);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var firstRecord = await first.Content.ReadFromJsonAsync<OrderFileResponse>();
        var secondRecord = await second.Content.ReadFromJsonAsync<OrderFileResponse>();
        Assert.NotNull(firstRecord);
        Assert.NotNull(secondRecord);
        Assert.Equal(firstRecord, secondRecord);
        await using var scope = app.Services.CreateAsyncScope();
        var row = Assert.Single(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().ToArrayAsync());
        Assert.Equal(firstRecord, new OrderFileResponse(row.Id, row.OrderId, row.Bucket, row.ObjectName, row.CreatedDate, row.ModifiedDate));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<int> SeedOrderAsync(WebApplicationFactory<Program> app, string name)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var order = new Order
        {
            Name = name,
            Process = new Process { Name = "Metadata process", Category = new Category { Name = "Metadata category" } },
            Quantity = 1,
            Manufactured = 0,
        };
        database.Orders.Add(order);
        await database.SaveChangesAsync();
        return order.Id;
    }
}
