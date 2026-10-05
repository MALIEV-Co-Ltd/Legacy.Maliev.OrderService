using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderCalendarTurnaroundHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    // Independently derived from source SQL Server DATEDIFF(day, CreatedDate, FinishedDate).
    // FinishedDate is a date column; calendar-boundary count differs from elapsed 24-hour periods.
    [Theory]
    [InlineData("2026-10-03T23:59:59", "2026-10-04", 1)]
    [InlineData("2026-10-03T23:59:59", "2026-10-03", 0)]
    [InlineData("2026-12-31T23:59:59", "2027-01-01", 1)]
    [InlineData("2024-02-28T23:59:59", "2024-03-01", 2)]
    [InlineData("2026-10-04T00:00:01", "2026-10-03", -1)]
    [InlineData("2026-10-03T12:00:00", null, null)]
    public async Task DetailRead_ActualComputedColumn_PreservesSourceCalendarDayCount(string createdText, string? finishedText, int? expected)
    {
        await using var app = await fixture.AppAsync(null);
        int orderId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var category = new Category { Name = "Calendar acceptance" };
            var process = new Process { Name = "Calendar process", Category = category };
            var order = new Order
            {
                Name = "Calendar boundary",
                Process = process,
                Quantity = 1,
                Manufactured = 0,
                CreatedDate = DateTime.Parse(createdText, CultureInfo.InvariantCulture),
                ModifiedDate = DateTime.Parse(createdText, CultureInfo.InvariantCulture),
                FinishedDate = finishedText is null ? null : DateTime.Parse(finishedText, CultureInfo.InvariantCulture),
            };
            database.Orders.Add(order);
            await database.SaveChangesAsync();
            orderId = order.Id;
            database.ChangeTracker.Clear();
            Assert.Equal(expected, (await database.Orders.AsNoTracking().SingleAsync()).Turnaround);
        }
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token("legacy.orders.read"));
        using var response = await client.GetAsync($"/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var wire = (await response.Content.ReadFromJsonAsync<OrderResponse>())!;
        Assert.Equal(orderId, wire.Id);
        Assert.Equal(expected, wire.Turnaround);
        Assert.Equal(1, wire.Remaining);
    }
}
