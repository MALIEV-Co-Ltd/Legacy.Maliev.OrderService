using System.Net;
using System.Net.Http.Headers;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileCreateMissingParentHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    public async Task MissingParent_ReturnsOriginalLiteralWithoutCreatingMetadata(string accept)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.FilesWrite));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await client.PostAsync("/orders/999999/files?bucket=metadata&objectName=part.step", null, cancellation.Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(accept, response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(cancellation.Token);
        Assert.Equal("Order not found", accept == "application/json" ? System.Text.Json.JsonSerializer.Deserialize<string>(body) : body);
        Assert.Null(response.Headers.Location);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Empty(await database.Files.AsNoTracking().ToArrayAsync(cancellation.Token));
        Assert.Empty(await database.Orders.AsNoTracking().ToArrayAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingParentMessage_DoesNotBypassWriteAuthority(bool anonymous)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        if (!anonymous)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.FilesRead));
        using var response = await client.PostAsync("/orders/999999/files?bucket=metadata&objectName=part.step", null);

        Assert.Equal(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("Order not found", await response.Content.ReadAsStringAsync());
        await using var scope = app.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<OrderDbContext>().Files.AsNoTracking().ToArrayAsync());
    }
}
