using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

/// <summary>Preserves the original duplicate-tolerant status-name read contract.</summary>
/// <param name="fixture">The existing isolated production HTTP fixture.</param>
public sealed class OrderStatusNameLookupSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    /// <summary>Duplicate definitions return one exact matching row without imposing a new ordering.</summary>
    [Fact]
    public async Task DuplicateNamesReturnOneMatchingDefinitionAndLeaveIdReadsAndStoresUnchanged()
    {
        const string name = "Operational duplicate";
        await using var app = await fixture.AppAsync(null);
        var before = await SeedAsync(app, name, name, "Unrelated operational");
        using var reader = Client(app, OrderPermissions.StatusRead);
        using var response = await reader.GetAsync("/orderstatuses/" + Uri.EscapeDataString(name));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = Assert.IsType<OrderStatusResponse>(await response.Content.ReadFromJsonAsync<OrderStatusResponse>());
        var candidates = before.Where(row => row.Name == name).ToArray();
        Assert.Equal(2, candidates.Length);
        Assert.Contains(value, candidates);
        using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "CreatedDate", "Description", "Id", "ModifiedDate", "Name" },
            wire.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var candidate in candidates)
        {
            using var detail = await reader.GetAsync($"/orderstatuses/{candidate.Id}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.Equal(candidate, await detail.Content.ReadFromJsonAsync<OrderStatusResponse>());
        }
        await AssertReadOnlyAsync(app, before);
    }

    /// <summary>Unique and absent names retain their existing 200 and 404 responses.</summary>
    [Fact]
    public async Task UniqueAndMissingNamesRetainDefinitionAndNotFoundWithoutWrites()
    {
        const string name = "Single operational";
        await using var app = await fixture.AppAsync(null);
        var before = await SeedAsync(app, name);
        using var reader = Client(app, OrderPermissions.StatusRead);
        using var found = await reader.GetAsync("/orderstatuses/" + Uri.EscapeDataString(name));
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        Assert.Equal(Assert.Single(before), await found.Content.ReadFromJsonAsync<OrderStatusResponse>());
        using var missing = await reader.GetAsync("/orderstatuses/Absent-operational");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertReadOnlyAsync(app, before);
    }

    /// <summary>Authentication and the exact status-read permission protect duplicate-name reads.</summary>
    /// <param name="permission">The supplied unrelated permission, or no token.</param>
    /// <param name="expected">The original access-denial status.</param>
    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    public async Task DuplicateNameReadRequiresAuthenticationAndStatusPermission(string? permission, HttpStatusCode expected)
    {
        const string name = "Private operational";
        await using var app = await fixture.AppAsync(null);
        var before = await SeedAsync(app, name, name);
        using var reader = Client(app, permission);
        using var response = await reader.GetAsync("/orderstatuses/" + Uri.EscapeDataString(name));
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(name, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await AssertReadOnlyAsync(app, before);
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<OrderStatusResponse[]> SeedAsync(WebApplicationFactory<Program> app, params string[] names)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        for (var index = 0; index < names.Length; index++)
        {
            statuses.Statuses.Add(new OrderStatus
            {
                Name = names[index],
                Description = "Definition " + index,
                CreatedDate = new DateTime(2020, 1, 1).AddDays(index),
                ModifiedDate = new DateTime(2021, 1, 1).AddDays(index),
            });
        }
        await statuses.SaveChangesAsync();
        return await SnapshotAsync(statuses);
    }

    private static Task<OrderStatusResponse[]> SnapshotAsync(OrderStatusDbContext statuses) =>
        statuses.Statuses.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new OrderStatusResponse(row.Id, row.Name, row.Description, row.CreatedDate, row.ModifiedDate))
            .ToArrayAsync();

    private static async Task AssertReadOnlyAsync(WebApplicationFactory<Program> app, OrderStatusResponse[] before)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Equal(before, await SnapshotAsync(statuses));
        Assert.False(await statuses.History.AnyAsync());
        Assert.False(await statuses.Transitions.AnyAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }
}
