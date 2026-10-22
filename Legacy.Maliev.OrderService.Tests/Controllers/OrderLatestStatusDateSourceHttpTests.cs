using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderLatestStatusDateSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LatestUsesStatusDefinitionDatesAndHighestHistoryIdWithoutChangingTimeline(bool nullDefinitionDates)
    {
        const int orderId = 42701;
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var definitionCreated = new DateTime(2018, 2, 3, 4, 5, 6);
        var definitionModified = definitionCreated.AddDays(2);
        var older = new OrderStatus { Name = "Earlier", Description = "Earlier definition" };
        var latest = new OrderStatus
        {
            Name = "In Progress",
            Description = "Current definition",
            CreatedDate = nullDefinitionDates ? null : definitionCreated,
            ModifiedDate = nullDefinitionDates ? null : definitionModified,
        };
        var unrelated = new OrderStatus { Name = "Unrelated", Description = "Other order" };
        statuses.AddRange(older, latest, unrelated);
        await statuses.SaveChangesAsync();
        var first = new OrderStatusHistory
        {
            OrderId = orderId,
            OrderStatusId = older.Id,
            CreatedDate = new DateTime(2025, 1, 1),
            ModifiedDate = new DateTime(2025, 1, 2),
        };
        statuses.History.Add(first);
        await statuses.SaveChangesAsync();
        var last = new OrderStatusHistory
        {
            OrderId = orderId,
            OrderStatusId = latest.Id,
            CreatedDate = new DateTime(2024, 1, 1),
            ModifiedDate = new DateTime(2024, 1, 2),
        };
        statuses.History.Add(last);
        await statuses.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory
        {
            OrderId = orderId + 1,
            OrderStatusId = unrelated.Id,
            CreatedDate = new DateTime(2026, 1, 1),
            ModifiedDate = new DateTime(2026, 1, 2),
        });
        await statuses.SaveChangesAsync();
        if (nullDefinitionDates)
        {
            // INSERT defaults populate generated date columns; clear the stored values
            // explicitly so this case exercises actual nullable definition dates.
            Assert.Equal(1, await statuses.Statuses.Where(x => x.Id == latest.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.CreatedDate, (DateTime?)null)
                    .SetProperty(x => x.ModifiedDate, (DateTime?)null)));
            await statuses.Entry(latest).ReloadAsync();
            Assert.Null(latest.CreatedDate);
            Assert.Null(latest.ModifiedDate);
        }
        Assert.True(last.Id > first.Id);
        var beforeDefinitions = await statuses.Statuses.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new OrderStatusResponse(x.Id, x.Name, x.Description, x.CreatedDate, x.ModifiedDate)).ToArrayAsync();
        var beforeHistory = await HistoryAsync(statuses);

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusRead));
        using var response = await client.GetAsync($"/orderstatuses/histories/{orderId}/latest");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var expected = new OrderStatusResponse(latest.Id, latest.Name, latest.Description, latest.CreatedDate, latest.ModifiedDate);
        Assert.Equal(expected, JsonSerializer.Deserialize<OrderStatusResponse>(body));
        using var document = JsonDocument.Parse(body);
        Assert.Equal(!nullDefinitionDates, document.RootElement.TryGetProperty("CreatedDate", out _));
        Assert.Equal(!nullDefinitionDates, document.RootElement.TryGetProperty("ModifiedDate", out _));

        using var timeline = await client.GetAsync($"/orderstatuses/histories/{orderId}");
        Assert.Equal(HttpStatusCode.OK, timeline.StatusCode);
        var events = JsonSerializer.Deserialize<OrderStatusHistoryResponse[]>(await timeline.Content.ReadAsStringAsync())!;
        var eventRow = Assert.Single(events, x => x.Id == last.Id);
        Assert.Equal(last.CreatedDate, eventRow.CreatedDate);
        Assert.Equal(last.ModifiedDate, eventRow.ModifiedDate);
        Assert.Equal(beforeDefinitions, await statuses.Statuses.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new OrderStatusResponse(x.Id, x.Name, x.Description, x.CreatedDate, x.ModifiedDate)).ToArrayAsync());
        Assert.Equal(beforeHistory, await HistoryAsync(statuses));
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData(OrderPermissions.CatalogRead, HttpStatusCode.Forbidden)]
    [InlineData(OrderPermissions.StatusRead, HttpStatusCode.NotFound)]
    public async Task LatestRetainsAuthenticationPermissionAndMissingHistoryResponses(string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        using var response = await client.GetAsync("/orderstatuses/histories/42701/latest");
        Assert.Equal(expected, response.StatusCode);
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.Empty(await statuses.Statuses.AsNoTracking().ToArrayAsync());
        Assert.Empty(await statuses.History.AsNoTracking().ToArrayAsync());
        Assert.False(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AnyAsync());
    }

    private static Task<OrderStatusHistoryResponse[]> HistoryAsync(OrderStatusDbContext statuses) =>
        statuses.History.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new OrderStatusHistoryResponse(x.Id, x.OrderId, x.OrderStatusId,
                x.OrderStatus!.Name, x.OrderStatus.Description, x.CreatedDate, x.ModifiedDate)).ToArrayAsync();
}
