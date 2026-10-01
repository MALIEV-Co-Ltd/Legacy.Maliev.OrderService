using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderNullDiscountProducerHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("0", "9750.75")]
    [InlineData("12.5", "8531.91")]
    public async Task RegisteredCreate_PreservesNullableDiscountAndComputedMoney(string? discountText, string? subtotalText)
    {
        var discount = Money(discountText);
        var subtotal = Money(subtotalText);
        await using var app = await fixture.AppAsync(null);
        var processId = await SeedProcessAsync(app);
        using var client = app.CreateClient();
        var operationKey = Guid.NewGuid().ToString("N");
        using var response = await CreateAsync(client, processId, discount, operationKey, "legacy.orders.create");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = Assert.IsType<OrderResponse>(await response.Content.ReadFromJsonAsync<OrderResponse>());
        AssertMoney(created, discount, subtotal);

        await using var fresh = app.Services.CreateAsyncScope();
        var stored = await fresh.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync();
        Assert.Equal(created.Id, stored.Id);
        Assert.Equal(discount, stored.DiscountPercent);
        Assert.Equal(subtotal, stored.Subtotal);
        Assert.Equal(3250.25m, stored.UnitPrice);
        Assert.Equal(3, stored.Quantity);
        Assert.Equal(3, stored.Remaining);
        Assert.Equal(42, stored.CustomerId);
        Assert.Equal(9001, stored.MaterialId);
        Assert.Equal(9002, stored.SurfaceFinishId);
        Assert.Equal(9003, stored.ColorId);
        Assert.Equal(764, stored.CurrencyId);
        Assert.Equal(operationKey, stored.OperationKey);
        Assert.False(stored.AllowPayment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealRedisReplay_AndChangedDiscountConflict_DoNotCreateAnotherOrder(bool zero)
    {
        await using var app = await fixture.AppAsync(null);
        var processId = await SeedProcessAsync(app);
        using var client = app.CreateClient();
        var key = Guid.NewGuid().ToString("N");
        decimal? discount = zero ? 0m : null;
        using var first = await CreateAsync(client, processId, discount, key, "legacy.orders.create");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = Assert.IsType<OrderResponse>(await first.Content.ReadFromJsonAsync<OrderResponse>());
        using var replay = await CreateAsync(client, processId, discount, key, "legacy.orders.create");
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadFromJsonAsync<OrderResponse>());
        using var conflict = await CreateAsync(client, processId, zero ? null : 0m, key, "legacy.orders.create");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var fresh = app.Services.CreateAsyncScope();
        var stored = await fresh.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(discount, stored.DiscountPercent);
        Assert.Equal(original.Subtotal, stored.Subtotal);
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("legacy.orders.read", HttpStatusCode.Forbidden)]
    public async Task MissingOrWrongCreatePermission_DoesNotWrite(string? permission, HttpStatusCode status)
    {
        await using var app = await fixture.AppAsync(null);
        var processId = await SeedProcessAsync(app);
        using var client = app.CreateClient();
        using var response = await CreateAsync(client, processId, null, Guid.NewGuid().ToString("N"), permission);
        Assert.Equal(status, response.StatusCode);
        await using var fresh = app.Services.CreateAsyncScope();
        Assert.Empty(await fresh.ServiceProvider.GetRequiredService<OrderDbContext>().Orders.AsNoTracking().ToListAsync());
    }

    private async Task<HttpResponseMessage> CreateAsync(HttpClient client, int processId, decimal? discount, string key, string? permission)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        if (permission is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        request.Headers.Add("Idempotency-Key", key);
        // Independent literal shape of CustomerOrderSubmissionTransport.CreateOrderRequest.
        request.Content = JsonContent.Create(new
        {
            customerId = 42,
            employeeId = (int?)null,
            name = "owned-additive-part.stl",
            description = "Owned additive producer probe",
            processId,
            materialId = 9001,
            surfaceFinishId = 9002,
            colorId = 9003,
            quantity = 3,
            manufactured = 0,
            unitPrice = 3250.25m,
            discountPercent = discount,
            currencyId = 764,
            leadTime = 2,
            promisedDate = (DateTime?)null,
            finishedDate = (DateTime?)null,
            comment = "Customer estimate only; staff review required before payment.",
            allowSocialMedia = false,
            allowCancellation = false,
            allowPayment = false,
            trackingNumber = (string?)null,
            operationKey = key,
        });
        return await client.SendAsync(request);
    }

    private static async Task<int> SeedProcessAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var process = new Process { Name = "Owned FDM", Category = new Category { Name = "Owned additive" } };
        context.Add(process);
        await context.SaveChangesAsync();
        return process.Id;
    }

    private static decimal? Money(string? value) => value is null ? null
        : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertMoney(OrderResponse order, decimal? discount, decimal? subtotal)
    {
        Assert.Equal(3250.25m, order.UnitPrice);
        Assert.Equal(3, order.Quantity);
        Assert.Equal(3, order.Remaining);
        Assert.Equal(discount, order.DiscountPercent);
        Assert.Equal(subtotal, order.Subtotal);
        Assert.False(order.AllowPayment);
        Assert.NotNull(order.CreatedDate);
        Assert.NotNull(order.ModifiedDate);
    }
}
