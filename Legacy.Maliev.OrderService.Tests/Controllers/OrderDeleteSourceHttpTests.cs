using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderDeleteSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    // Initial source 5fac706a7983a6d359b39acbd670e6800afe020e: each DELETE finds
    // the exact row, returns404 when absent, otherwise removes it and returns204.
    // Referenced-entity failures and modern lifetime guards remain separate contracts.
    [Theory]
    [InlineData("category")]
    [InlineData("format")]
    [InlineData("process")]
    [InlineData("status")]
    [InlineData("history")]
    public async Task DeletePreservesSourceEmptySuccessAndMissingResponsesWithoutChangingOtherRows(string kind)
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var target = await SeedAsync(app, kind, token);
        var expected = await SnapshotAsync(app, token, kind, target.Id);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(target.Permission));

        using var deleted = await client.DeleteAsync(target.Route, token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync(token));
        Assert.Equal(expected, await SnapshotAsync(app, token));

        using var repeated = await client.DeleteAsync(target.Route, token);
        using var missing = await client.DeleteAsync(Route(kind, int.MaxValue), token);
        Assert.Equal(HttpStatusCode.NotFound, repeated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(expected, await SnapshotAsync(app, token));
    }

    [Theory]
    [InlineData("category", false)]
    [InlineData("category", true)]
    [InlineData("format", false)]
    [InlineData("format", true)]
    [InlineData("process", false)]
    [InlineData("process", true)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("history", false)]
    [InlineData("history", true)]
    [InlineData("file", false)]
    public async Task DeleteAuthorityRefusesBeforeChangingEitherDatabase(string kind, bool wrongPermission)
    {
        await using var app = await fixture.AppAsync(null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var target = await SeedAsync(app, kind, token);
        var before = await SnapshotAsync(app, token);
        using var client = app.CreateClient();
        if (wrongPermission)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.Read));

        using var response = await client.DeleteAsync(target.Route, token);
        Assert.Equal(wrongPermission ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app, token));
    }

    private static async Task<Target> SeedAsync(WebApplicationFactory<Program> app, string kind, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var category = new Category { Name = "Retained category" };
        var process = new Process { Name = "Retained process", Category = category };
        var first = new Order { Name = "Retained first order", Process = process, Quantity = 1 };
        var second = new Order { Name = "Retained second order", Process = process, Quantity = 2 };
        orders.AddRange(first, second, new FileFormat { Name = "Retained format", Extension = ".step" });
        await orders.SaveChangesAsync(token);
        orders.Files.AddRange(
            new OrderFile { OrderId = first.Id, Bucket = "retained", ObjectName = "first.step" },
            new OrderFile { OrderId = second.Id, Bucket = "retained", ObjectName = "second.step" });
        await orders.SaveChangesAsync(token);
        var initial = new OrderStatus { Name = "Retained initial", Description = "Original definition" };
        var next = new OrderStatus { Name = "Retained next", Description = "Possible definition" };
        statuses.AddRange(initial, next);
        await statuses.SaveChangesAsync(token);
        statuses.Transitions.Add(new OrderStatusTransition { OrderStatusId = initial.Id, PossibleStatusId = next.Id });
        statuses.History.AddRange(
            new OrderStatusHistory { OrderId = first.Id, OrderStatusId = initial.Id },
            new OrderStatusHistory { OrderId = second.Id, OrderStatusId = next.Id });
        await statuses.SaveChangesAsync(token);

        int id;
        switch (kind)
        {
            case "category":
                var targetCategory = new Category { Name = "Unreferenced delete target" };
                orders.Categories.Add(targetCategory);
                await orders.SaveChangesAsync(token);
                id = targetCategory.Id;
                break;
            case "format":
                var targetFormat = new FileFormat { Name = "Delete target", Extension = ".stl" };
                orders.FileFormats.Add(targetFormat);
                await orders.SaveChangesAsync(token);
                id = targetFormat.Id;
                break;
            case "process":
                var targetProcess = new Process { Name = "Unreferenced delete target", CategoryId = category.Id };
                orders.Processes.Add(targetProcess);
                await orders.SaveChangesAsync(token);
                id = targetProcess.Id;
                break;
            case "status":
                var targetStatus = new OrderStatus { Name = "Unreferenced delete target" };
                statuses.Statuses.Add(targetStatus);
                await statuses.SaveChangesAsync(token);
                id = targetStatus.Id;
                break;
            case "history":
                var targetHistory = new OrderStatusHistory { OrderId = first.Id, OrderStatusId = next.Id };
                statuses.History.Add(targetHistory);
                await statuses.SaveChangesAsync(token);
                id = targetHistory.Id;
                break;
            case "file":
                var targetFile = new OrderFile { OrderId = first.Id, Bucket = "delete-target", ObjectName = "target.step" };
                orders.Files.Add(targetFile);
                await orders.SaveChangesAsync(token);
                id = targetFile.Id;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var permission = kind switch
        {
            "status" or "history" => OrderPermissions.StatusDelete,
            "file" => OrderPermissions.FilesDelete,
            _ => OrderPermissions.CatalogDelete,
        };
        return new Target(id, Route(kind, id), permission);
    }

    private static string Route(string kind, int id) => kind switch
    {
        "category" => $"/orders/Categories/{id}",
        "format" => $"/orders/FileFormats/{id}",
        "process" => $"/orders/Processes/{id}",
        "status" => $"/OrderStatuses/{id}",
        "history" => $"/orderstatuses/Histories/{id}",
        "file" => $"/orders/Files/{id}",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token,
        string? excludedKind = null, int excludedId = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        // Separate no-tracking queries retain every stored scalar without loading cyclic navigations.
        return JsonSerializer.Serialize(new
        {
            Orders = await orders.Orders.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(token),
            Categories = await orders.Categories.AsNoTracking()
                .Where(x => excludedKind != "category" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            Formats = await orders.FileFormats.AsNoTracking()
                .Where(x => excludedKind != "format" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            Processes = await orders.Processes.AsNoTracking()
                .Where(x => excludedKind != "process" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            Files = await orders.Files.AsNoTracking()
                .Where(x => excludedKind != "file" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking()
                .Where(x => excludedKind != "status" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking()
                .Where(x => excludedKind != "history" || x.Id != excludedId).OrderBy(x => x.Id).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(token),
            DeletionIntents = await orders.DeletionIntents.AsNoTracking().OrderBy(x => x.OrderId).ToArrayAsync(token),
        });
    }

    private sealed record Target(int Id, string Route, string Permission);
}
