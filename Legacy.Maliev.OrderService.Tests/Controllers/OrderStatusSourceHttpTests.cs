using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderStatusSourceHttpTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ExactUtf16BoundaryPersists_OneUnitOverflowDoesNotWrite(bool put, bool description, bool supplementary)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, OrderPermissions.StatusWrite);
        var id = put ? await SeedAsync(client) : 0;
        var limit = description ? 100 : 50;
        var exact = supplementary ? string.Concat(Enumerable.Repeat("\U0001F600", limit / 2)) : new string('\u0E01', limit);
        Assert.Equal(limit, exact.Length);
        var request = description ? new UpsertOrderStatusRequest("Boundary", exact) : new UpsertOrderStatusRequest(exact, "Definition");
        using var accepted = await WriteAsync(client, put, id, request);
        Assert.Equal(put ? HttpStatusCode.NoContent : HttpStatusCode.Created, accepted.StatusCode);
        if (!put) id = Assert.IsType<OrderStatusResponse>(await accepted.Content.ReadFromJsonAsync<OrderStatusResponse>()).Id;
        var before = await SnapshotAsync(app);
        var stored = Assert.Single(before);
        Assert.Equal(id, stored.Id);
        Assert.Equal(request.Name, stored.Name);
        Assert.Equal(request.Description, stored.Description);
        Assert.NotNull(stored.CreatedDate);
        using var reader = Client(app, OrderPermissions.StatusRead);
        Assert.Equal(stored, await reader.GetFromJsonAsync<OrderStatusResponse>($"/orderstatuses/{id}"));
        var overflow = exact + " "; // A trailing unit counts; it must not be trimmed before admission.
        var rejectedRequest = description ? new UpsertOrderStatusRequest("Changed", overflow) : new UpsertOrderStatusRequest(overflow, "Changed");
        using var rejected = await WriteAsync(client, put, id, rejectedRequest);
        await AssertSafeBadRequestAsync(rejected, overflow);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NullNameIsRejectedWithoutChangingRowsOrTimestamps(bool put)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, OrderPermissions.StatusWrite);
        var id = put ? await SeedAsync(client) : 0;
        var before = await SnapshotAsync(app);
        using var response = await WriteAsync(client, put, id, new UpsertOrderStatusRequest(null, "Changed"));
        await AssertSafeBadRequestAsync(response, "Changed");
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(false, "", null)]
    [InlineData(false, "", "")]
    [InlineData(false, "  \t\u2003  ", " \t\u2003 ")]
    [InlineData(false, "  Draft  ", "  Source definition  ")]
    [InlineData(true, "", null)]
    [InlineData(true, "", "")]
    [InlineData(true, "  \t\u2003  ", " \t\u2003 ")]
    [InlineData(true, "  Draft  ", "  Source definition  ")]
    public async Task NullDescriptionAndEmptyOrPaddedStringsPreserveTheirExactValues(bool put, string name, string? description)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, OrderPermissions.StatusWrite);
        var id = put ? await SeedAsync(client) : 0;
        var original = put ? Assert.Single(await SnapshotAsync(app)) : null;
        using var response = await WriteAsync(client, put, id, new UpsertOrderStatusRequest(name, description));
        Assert.Equal(put ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        var stored = Assert.Single(await SnapshotAsync(app));
        Assert.Equal(name, stored.Name);
        Assert.Equal(description, stored.Description);
        Assert.NotNull(stored.CreatedDate);
        if (original is not null)
        {
            Assert.Equal(original.Id, stored.Id);
            Assert.Equal(original.CreatedDate, stored.CreatedDate);
        }
        using var reader = Client(app, OrderPermissions.StatusRead);
        Assert.Equal(stored, await reader.GetFromJsonAsync<OrderStatusResponse>($"/orderstatuses/{stored.Id}"));
    }

    [Theory]
    [InlineData(false, null, HttpStatusCode.Unauthorized)]
    [InlineData(true, null, HttpStatusCode.Unauthorized)]
    [InlineData(false, OrderPermissions.StatusRead, HttpStatusCode.Forbidden)]
    [InlineData(true, OrderPermissions.StatusRead, HttpStatusCode.Forbidden)]
    public async Task AuthenticationAndPermissionPrecedeInvalidInputWithoutWrites(bool put, string? permission, HttpStatusCode expected)
    {
        await using var app = await fixture.AppAsync(null);
        using var writer = Client(app, OrderPermissions.StatusWrite);
        var id = put ? await SeedAsync(writer) : 0;
        var before = await SnapshotAsync(app);
        using var client = Client(app, permission);
        using var response = await WriteAsync(client, put, id, new UpsertOrderStatusRequest(null, new string('X', 101)));
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingPutRemainsNotFoundBeforeNullOrOverflowValidation(bool nullName)
    {
        await using var app = await fixture.AppAsync(null);
        using var client = Client(app, OrderPermissions.StatusWrite);
        await SeedAsync(client);
        var before = await SnapshotAsync(app);
        var request = nullName ? new UpsertOrderStatusRequest(null, "Changed") : new UpsertOrderStatusRequest(new string('N', 51), new string('D', 101));
        using var response = await WriteAsync(client, true, int.MaxValue, request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, await SnapshotAsync(app));
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(new[] { "details", "error", "statusCode", "traceId" }, root.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("The request is invalid.", root.GetProperty("error").GetString());
        Assert.Equal(400, root.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
    }

    private HttpClient Client(WebApplicationFactory<Program> app, string? permission)
    {
        var client = app.CreateClient();
        if (permission is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(permission));
        return client;
    }

    private static async Task<int> SeedAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/orderstatuses", new UpsertOrderStatusRequest("Original", "Original definition"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return Assert.IsType<OrderStatusResponse>(await response.Content.ReadFromJsonAsync<OrderStatusResponse>()).Id;
    }

    private static Task<HttpResponseMessage> WriteAsync(HttpClient client, bool put, int id, UpsertOrderStatusRequest request) =>
        put ? client.PutAsJsonAsync($"/orderstatuses/{id}", request) : client.PostAsJsonAsync("/orderstatuses", request);

    private static async Task AssertSafeBadRequestAsync(HttpResponseMessage response, string submittedValue)
    {
        await AssertInvalidAsync(response);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        Assert.Equal(400, body.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("details").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        Assert.DoesNotContain(submittedValue, body.RootElement.ToString(), StringComparison.Ordinal);
    }

    private static async Task<OrderStatusResponse[]> SnapshotAsync(WebApplicationFactory<Program> app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>().Statuses.AsNoTracking().OrderBy(row => row.Id)
            .Select(row => new OrderStatusResponse(row.Id, row.Name, row.Description, row.CreatedDate, row.ModifiedDate)).ToArrayAsync();
    }
}
