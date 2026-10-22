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

public sealed class OrderCatalogLifecycleHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private async Task<WebApplicationFactory<Program>> AppAsync()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero));
        return await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string permission)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    [Fact]
    public async Task Category_LifecycleResolvesLocationAndDeletesOnlyTheOwnedRow()
    {
        await using var app = await AppAsync();
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        using var deleter = Client(app, OrderPermissions.CatalogDelete);
        using var created = await writer.PostAsJsonAsync("/orders/categories", new UpsertCategoryRequest("Source category"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var value = (await created.Content.ReadFromJsonAsync<CategoryResponse>())!;
        Assert.Equal("Source category", value.Name);
        Assert.NotNull(value.CreatedDate);
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(value, await reader.GetFromJsonAsync<CategoryResponse>(created.Headers.Location));
        using var updated = await writer.PutAsJsonAsync($"/orders/categories/{value.Id}", new UpsertCategoryRequest("Changed category"));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        Assert.Equal("Changed category", (await reader.GetFromJsonAsync<CategoryResponse>($"/orders/categories/{value.Id}"))!.Name);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Equal("Changed category", (await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Categories.AsNoTracking().SingleAsync()).Name);
        using var deleted = await deleter.DeleteAsync($"/orders/categories/{value.Id}");
        using var missing = await reader.GetAsync($"/orders/categories/{value.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Categories.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task FileFormat_LifecyclePreservesExtensionAndListVisibility()
    {
        await using var app = await AppAsync();
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        using var deleter = Client(app, OrderPermissions.CatalogDelete);
        using var empty = await reader.GetAsync("/orders/fileformats");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await writer.PostAsJsonAsync("/orders/fileformats", new UpsertFileFormatRequest("STEP", ".step"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var value = (await created.Content.ReadFromJsonAsync<FileFormatResponse>())!;
        Assert.Equal(".step", value.Extension);
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(value, await reader.GetFromJsonAsync<FileFormatResponse>(created.Headers.Location));
        using var updated = await writer.PutAsJsonAsync($"/orders/fileformats/{value.Id}", new UpsertFileFormatRequest("STL", ".stl"));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var current = Assert.Single((await reader.GetFromJsonAsync<FileFormatResponse[]>("/orders/fileformats"))!);
        Assert.Equal(value.Id, current.Id);
        Assert.Equal("STL", current.Name);
        Assert.Equal(".stl", current.Extension);
        using var deleted = await deleter.DeleteAsync($"/orders/fileformats/{value.Id}");
        using var missing = await reader.GetAsync($"/orders/fileformats/{value.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().FileFormats.ToArrayAsync());
    }

    [Theory]
    [InlineData("Additive", "additive")]
    [InlineData("Electronics", "electronics")]
    [InlineData("Machining", "machining")]
    [InlineData("Scanning", "scanning")]
    public async Task Process_LifecyclePreservesCategoryScopedLists(string categoryName, string segment)
    {
        const string createdName = "  Source \u0e01\u0e23\u0e30\u0e1a\u0e27\u0e19\u0e01\u0e32\u0e23%_process  ";
        const string updatedName = "\tUpdated \u0e01\u0e23\u0e30\u0e1a\u0e27\u0e19\u0e01\u0e32\u0e23%_process\t";
        await using var app = await AppAsync();
        using var writer = Client(app, OrderPermissions.CatalogWrite);
        using var reader = Client(app, OrderPermissions.CatalogRead);
        using var deleter = Client(app, OrderPermissions.CatalogDelete);
        using var categoryResponse = await writer.PostAsJsonAsync("/orders/categories", new UpsertCategoryRequest(categoryName));
        Assert.Equal(HttpStatusCode.Created, categoryResponse.StatusCode);
        var category = (await categoryResponse.Content.ReadFromJsonAsync<CategoryResponse>())!;
        using var otherCategoryResponse = await writer.PostAsJsonAsync("/orders/categories", new UpsertCategoryRequest("Other category"));
        Assert.Equal(HttpStatusCode.Created, otherCategoryResponse.StatusCode);
        var otherCategory = (await otherCategoryResponse.Content.ReadFromJsonAsync<CategoryResponse>())!;
        using var otherProcessResponse = await writer.PostAsJsonAsync("/orders/processes", new UpsertProcessRequest(otherCategory.Id, "Other process"));
        Assert.Equal(HttpStatusCode.Created, otherProcessResponse.StatusCode);
        var otherProcess = (await otherProcessResponse.Content.ReadFromJsonAsync<ProcessResponse>())!;
        using var empty = await reader.GetAsync($"/orders/processes/{segment}");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await writer.PostAsJsonAsync("/orders/processes", new UpsertProcessRequest(category.Id, createdName));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var value = (await created.Content.ReadFromJsonAsync<ProcessResponse>())!;
        Assert.Equal(createdName, value.Name);
        Assert.Equal(category.Id, value.CategoryId);
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(value, await reader.GetFromJsonAsync<ProcessResponse>(created.Headers.Location));
        Assert.Equal(value, Assert.Single((await reader.GetFromJsonAsync<ProcessResponse[]>($"/orders/processes/{segment}"))!));
        var all = (await reader.GetFromJsonAsync<ProcessResponse[]>("/orders/processes"))!;
        Assert.Equal(2, all.Length);
        Assert.Contains(value, all);
        Assert.Contains(otherProcess, all);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(createdName, (await database.Processes.AsNoTracking().SingleAsync(row => row.Id == value.Id)).Name);
        var otherBefore = await database.Processes.AsNoTracking().Where(row => row.Id == otherProcess.Id)
            .Select(row => new { row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate }).SingleAsync();
        using var updated = await writer.PutAsJsonAsync($"/orders/processes/{value.Id}", new UpsertProcessRequest(category.Id, updatedName));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var current = (await reader.GetFromJsonAsync<ProcessResponse>($"/orders/processes/{value.Id}"))!;
        Assert.Equal(updatedName, current.Name);
        Assert.Equal(category.Id, current.CategoryId);
        Assert.Equal(value.CreatedDate, current.CreatedDate);
        Assert.Equal(current, Assert.Single((await reader.GetFromJsonAsync<ProcessResponse[]>($"/orders/processes/{segment}"))!));
        Assert.Contains(current, (await reader.GetFromJsonAsync<ProcessResponse[]>("/orders/processes"))!);
        Assert.Equal(updatedName, (await database.Processes.AsNoTracking().SingleAsync(row => row.Id == value.Id)).Name);
        using var deleted = await deleter.DeleteAsync($"/orders/processes/{value.Id}");
        using var missing = await reader.GetAsync($"/orders/processes/{value.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(otherProcess.Id, Assert.Single(await database.Processes.AsNoTracking().ToArrayAsync()).Id);
        Assert.Equal(otherBefore, await database.Processes.AsNoTracking().Where(row => row.Id == otherProcess.Id)
            .Select(row => new { row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate }).SingleAsync());
        Assert.Equal(2, await database.Categories.CountAsync());
    }

    [Fact]
    public async Task Status_LifecyclePreservesNameLookupAndConfiguredAvailableTransitions()
    {
        await using var app = await AppAsync();
        using var writer = Client(app, OrderPermissions.StatusWrite);
        using var reader = Client(app, OrderPermissions.StatusRead);
        using var deleter = Client(app, OrderPermissions.StatusDelete);
        using var empty = await reader.GetAsync("/orderstatuses");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        using var created = await writer.PostAsJsonAsync("/orderstatuses", new UpsertOrderStatusRequest("Draft", "Source definition"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var value = (await created.Content.ReadFromJsonAsync<OrderStatusResponse>())!;
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(value, await reader.GetFromJsonAsync<OrderStatusResponse>(created.Headers.Location));
        Assert.Equal(value, await reader.GetFromJsonAsync<OrderStatusResponse>("/orderstatuses/Draft"));
        using var updated = await writer.PutAsJsonAsync($"/orderstatuses/{value.Id}", new UpsertOrderStatusRequest("Reviewed", "Updated definition"));
        Assert.Equal(HttpStatusCode.NoContent, updated.StatusCode);
        var current = Assert.Single((await reader.GetFromJsonAsync<OrderStatusResponse[]>("/orderstatuses"))!);
        Assert.Equal("Reviewed", current.Name);
        Assert.Equal("Updated definition", current.Description);
        using var unavailable = await reader.GetAsync($"/orderstatuses/{value.Id}/available");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var next = new OrderStatus { Name = "Next" };
        database.Statuses.Add(next);
        await database.SaveChangesAsync();
        database.Transitions.Add(new OrderStatusTransition { OrderStatusId = value.Id, PossibleStatusId = next.Id });
        await database.SaveChangesAsync();
        var available = Assert.Single((await reader.GetFromJsonAsync<OrderStatusResponse[]>($"/orderstatuses/{value.Id}/available"))!);
        Assert.Equal(next.Id, available.Id);
        Assert.Equal("Next", available.Name);
        database.Transitions.RemoveRange(await database.Transitions.ToArrayAsync());
        await database.SaveChangesAsync();
        using var deleted = await deleter.DeleteAsync($"/orderstatuses/{value.Id}");
        using var missing = await reader.GetAsync($"/orderstatuses/{value.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(next.Id, Assert.Single(await database.Statuses.AsNoTracking().ToArrayAsync()).Id);
    }

    [Fact]
    public async Task InProgressShortcut_UsesSpacedSourceNameAndReplaysWithoutAnotherTransition()
    {
        await using var app = await AppAsync();
        var seed = await SeedInProgressAsync(app, sourcePresent: true, sourceAllowed: true);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        writer.DefaultRequestHeaders.Add("Idempotency-Key", "source-in-progress");
        var route = $"/orderstatuses/histories/{seed.OrderId}/InProgress";
        using var created = await writer.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var history = await database.History.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(2, history.Length);
        Assert.Equal(seed.InitialId, history[0].OrderStatusId);
        Assert.Equal(seed.SourceId, history[1].OrderStatusId);
        Assert.DoesNotContain(history, row => row.OrderStatusId == seed.CompactId);

        // Move the owned fixture back and remove its edges: only the stored replay may succeed.
        database.History.Add(new OrderStatusHistory { OrderId = seed.OrderId, OrderStatusId = seed.InitialId });
        database.Transitions.RemoveRange(await database.Transitions.ToArrayAsync());
        await database.SaveChangesAsync();
        var beforeReplay = await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync();
        using var replay = await writer.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(beforeReplay, await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync());
        writer.DefaultRequestHeaders.Remove("Idempotency-Key");
        writer.DefaultRequestHeaders.Add("Idempotency-Key", "source-in-progress-new");
        using var refused = await writer.PostAsync(route, null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(beforeReplay, await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync());
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().DeletionIntents.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task InProgressShortcut_MissingSpacedNameDoesNotSubstituteCompactName()
    {
        await using var app = await AppAsync();
        var seed = await SeedInProgressAsync(app, sourcePresent: false, sourceAllowed: false);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var before = await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync();
        using var missing = await writer.PostAsync($"/orderstatuses/histories/{seed.OrderId}/InProgress", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(before, await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync());
    }

    [Fact]
    public async Task InProgressShortcut_SpacedTargetStillRequiresConfiguredTransition()
    {
        await using var app = await AppAsync();
        var seed = await SeedInProgressAsync(app, sourcePresent: true, sourceAllowed: false);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var before = await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync();
        using var refused = await writer.PostAsync($"/orderstatuses/histories/{seed.OrderId}/InProgress", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(before, await database.History.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new { row.Id, row.OrderId, row.OrderStatusId, row.CreatedDate, row.ModifiedDate }).ToArrayAsync());
    }

    private static async Task<(int OrderId, int InitialId, int SourceId, int CompactId)> SeedInProgressAsync(
        WebApplicationFactory<Program> app, bool sourcePresent, bool sourceAllowed)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var order = new Order
        {
            Name = "Owned source shortcut fixture",
            Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } },
            Quantity = 1
        };
        orders.Add(order);
        await orders.SaveChangesAsync();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var initial = new OrderStatus { Name = "New" };
        var compact = new OrderStatus { Name = "InProgress" };
        var source = new OrderStatus { Name = "In Progress" };
        statuses.AddRange(initial, compact);
        if (sourcePresent) statuses.Add(source);
        await statuses.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatusId = initial.Id });
        statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = initial.Id, PossibleStatusId = compact.Id });
        if (sourcePresent && sourceAllowed)
            statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = initial.Id, PossibleStatusId = source.Id });
        await statuses.SaveChangesAsync();
        return (order.Id, initial.Id, source.Id, compact.Id);
    }
}
