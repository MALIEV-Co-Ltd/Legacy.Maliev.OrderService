using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class ProcessSourceNameHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExactUtf16BoundaryPersistsAndOverflowDoesNotWrite(bool put, bool supplementary)
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        var (category, process) = await SeedAsync(writer);
        var exact = supplementary ? string.Concat(Enumerable.Repeat("😀", 24)) + "  " : new string('ก', 48) + "  ";
        Assert.Equal(50, exact.Length);
        using var valid = await SendAsync(writer, put, process.Id, category.Id, exact);
        Assert.Equal(put ? HttpStatusCode.NoContent : HttpStatusCode.Created, valid.StatusCode);
        var id = put ? process.Id : (await valid.Content.ReadFromJsonAsync<ProcessResponse>())!.Id;
        var readback = (await reader.GetFromJsonAsync<ProcessResponse>($"/orders/processes/{id}"))!;
        Assert.Equal(exact, readback.Name);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            Assert.Equal(exact, (await db.Processes.AsNoTracking().SingleAsync(row => row.Id == id)).Name);
        }
        var before = await GraphAsync(app);
        using var invalid = await SendAsync(writer, put, id, category.Id, exact + "ก");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(before, await GraphAsync(app));
        Assert.Equal(readback, await reader.GetFromJsonAsync<ProcessResponse>($"/orders/processes/{id}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" ก padded ")]
    public async Task EmptyAndPaddedNamesPreserveExactCreateAndUpdateValues(string name)
    {
        var instant = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var clock = new FakeTimeProvider(instant);
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        var (category, process) = await SeedAsync(writer);
        using var create = await SendAsync(writer, false, 0, category.Id, name);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<ProcessResponse>())!;
        Assert.Equal(name, created.Name);
        var createdTime = DateTime.SpecifyKind(instant.UtcDateTime, DateTimeKind.Unspecified);
        Assert.Equal(createdTime, created.CreatedDate);
        Assert.Equal(createdTime, created.ModifiedDate);
        Assert.NotNull(create.Headers.Location);
        var storedCreatedTime = new DateTime(createdTime.Ticks - createdTime.Ticks % 10, DateTimeKind.Unspecified);
        var readback = (await reader.GetFromJsonAsync<ProcessResponse>(create.Headers.Location))!;
        Assert.Equal(created with { CreatedDate = storedCreatedTime, ModifiedDate = storedCreatedTime }, readback);
        clock.Advance(TimeSpan.FromSeconds(1) + TimeSpan.FromTicks(2));
        using var update = await SendAsync(writer, true, process.Id, category.Id, name);
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
        var updatedTime = DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
        var storedUpdatedTime = new DateTime(updatedTime.Ticks - updatedTime.Ticks % 10, DateTimeKind.Unspecified);
        var updated = (await reader.GetFromJsonAsync<ProcessResponse>($"/orders/processes/{process.Id}"))!;
        Assert.Equal(new ProcessResponse(process.Id, category.Id, name, storedCreatedTime, storedUpdatedTime), updated);
        await using var scope = app.Services.CreateAsyncScope();
        var rows = await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Processes.AsNoTracking().ToArrayAsync();
        Assert.All(rows, row => Assert.Equal(name, row.Name));
        foreach (var row in rows)
        {
            var expected = row.Id == created.Id ? readback : updated;
            Assert.Equal(expected, new ProcessResponse(row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullNameRefusesWithoutChangingTheWholeGraph(bool put)
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var (category, process) = await SeedAsync(writer);
        var before = await GraphAsync(app);
        using var response = await SendAsync(writer, put, process.Id, category.Id, null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await GraphAsync(app));
    }

    [Theory]
    [InlineData(false, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, null, HttpStatusCode.Unauthorized)]
    [InlineData(false, OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData(true, OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndPermissionPrecedeOverflowWithoutWrites(bool put, string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var (category, process) = await SeedAsync(writer);
        using var caller = Client(app, permission);
        var before = await GraphAsync(app);
        using var response = await SendAsync(caller, put, process.Id, category.Id, new string('ก', 51));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await GraphAsync(app));
    }

    [Fact]
    public async Task MissingUpdateRemainsNotFoundBeforeRepositoryLengthValidation()
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var (category, _) = await SeedAsync(writer);
        var before = await GraphAsync(app);
        using var response = await SendAsync(writer, true, int.MaxValue, category.Id, new string('ก', 51));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await GraphAsync(app));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectRepositoryInvalidNamesAreRejectedBeforeTrackedMutation(bool put, bool nullName)
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        var (category, process) = await SeedAsync(writer);
        var before = await GraphAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var request = new UpsertProcessRequest(category.Id, nullName ? null! : string.Concat(Enumerable.Repeat("😀", 26)));
        if (put) await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateProcessAsync(process.Id, request, CancellationToken.None));
        else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProcessAsync(request, CancellationToken.None));
        Assert.Equal(before, await GraphAsync(app));
        Assert.DoesNotContain(scope.ServiceProvider.GetRequiredService<OrderDbContext>().ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(45);
        if (permission is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<(CategoryResponse Category, ProcessResponse Process)> SeedAsync(HttpClient writer)
    {
        using var category = await writer.PostAsJsonAsync("/orders/categories", new UpsertCategoryRequest("Synthetic category"));
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var value = (await category.Content.ReadFromJsonAsync<CategoryResponse>())!;
        using var process = await SendAsync(writer, false, 0, value.Id, "Synthetic process");
        Assert.Equal(HttpStatusCode.Created, process.StatusCode);
        return (value, (await process.Content.ReadFromJsonAsync<ProcessResponse>())!);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, bool put, int id, int category, string? name)
    {
        var payload = new Dictionary<string, object?> { ["CategoryId"] = category, ["Name"] = name };
        return put ? client.PutAsJsonAsync($"/orders/processes/{id}", payload) : client.PostAsJsonAsync("/orders/processes", payload);
    }

    private static async Task<string> GraphAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        return JsonSerializer.Serialize(new
        {
            categories = await db.Categories.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(lifetime.Token),
            processes = await db.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(lifetime.Token),
            orders = await db.Orders.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(lifetime.Token),
            files = await db.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(lifetime.Token),
            formats = await db.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(lifetime.Token),
            intents = await db.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(lifetime.Token),
        });
    }
}
