using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class FileFormatResponseWireContractTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResponseMatchesOriginalMvcNamesAndExistingWebReader(bool nulls)
    {
        var value = new FileFormatResponse(17, nulls ? null : "  ไทย STEP  ",
            nulls ? null : ".StEp", nulls ? null : new DateTime(2024, 1, 2, 3, 4, 5),
            nulls ? null : new DateTime(2024, 2, 3, 4, 5, 6));
        var actual = JsonSerializer.Serialize(value, ResponseOptions());
        using var wire = JsonDocument.Parse(actual);
        AssertSourceWire(wire.RootElement, value);
        using var content = new StringContent(actual, Encoding.UTF8, "application/json");
        // Exact three-field shape of the inspected Web CustomerOrderFileFormat reader.
        var consumer = await content.ReadFromJsonAsync<WebFileFormatConsumer>();
        Assert.Equal(new WebFileFormatConsumer(value.Id, value.Name, value.Extension), consumer);
        Assert.True(JsonSerializerOptions.Web.PropertyNameCaseInsensitive);
        using var request = JsonDocument.Parse(JsonSerializer.Serialize(
            new UpsertFileFormatRequest("STEP", ".step"), ResponseOptions()));
        Assert.Equal(new[] { "Extension", "Name" },
            request.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        using var category = JsonDocument.Parse(JsonSerializer.Serialize(
            new CategoryResponse(3, "Control", null, null), ResponseOptions()));
        Assert.True(category.RootElement.TryGetProperty("Id", out _));
        Assert.False(category.RootElement.TryGetProperty("id", out _));
    }

    [Fact]
    public async Task PostLocationDetailAndListExposeOnlySourceFileFormatNames()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 22, 1, 2, 3, TimeSpan.Zero));
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        using var writer = app.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogWrite));
        using var content = new StringContent(
            "{\"nAmE\":\"  ไทย STEP  \",\"eXtEnSiOn\":\".StEp\",\"Id\":99999999,\"CreatedDate\":\"2000-01-01T00:00:00Z\",\"ModifiedDate\":\"2000-01-01T00:00:00Z\"}",
            Encoding.UTF8, "application/json");
        using var created = await writer.PostAsync("/orders/fileformats", content);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var postBody = await created.Content.ReadAsStringAsync();
        using var consumerBody = new StringContent(postBody, Encoding.UTF8, "application/json");
        var value = (await consumerBody.ReadFromJsonAsync<FileFormatResponse>())!;
        Assert.NotEqual(99999999, value.Id);
        Assert.Equal("  ไทย STEP  ", value.Name);
        Assert.Equal(".StEp", value.Extension);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, value.CreatedDate);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, value.ModifiedDate);
        Assert.Equal(DateTimeKind.Utc, value.CreatedDate!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, value.ModifiedDate!.Value.Kind);
        var fresh = new FileFormatResponse(value.Id, "  ไทย STEP  ", ".StEp",
            clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime);
        using var postWire = JsonDocument.Parse(postBody);
        AssertSourceWire(postWire.RootElement, fresh);
        var stored = fresh with
        {
            CreatedDate = DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified),
            ModifiedDate = DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified),
        };
        Assert.NotNull(created.Headers.Location);
        using var reader = app.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        using var detail = await reader.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var detailWire = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        AssertSourceWire(detailWire.RootElement, stored);
        using var list = await reader.GetAsync("/orders/fileformats");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listWire = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        AssertSourceWire(Assert.Single(listWire.RootElement.EnumerateArray()), stored);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        Assert.Equal(stored, await database.FileFormats.AsNoTracking()
            .Select(x => new FileFormatResponse(x.Id, x.Name, x.Extension, x.CreatedDate, x.ModifiedDate)).SingleAsync());
    }

    [Fact]
    public async Task StoredNullsAreOmittedFromDetailAndListWithoutChangingDatabase()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var row = new FileFormat();
        database.FileFormats.Add(row);
        await database.SaveChangesAsync();
        // INSERT date defaults must be cleared in the actual stored row.
        Assert.Equal(1, await database.FileFormats.Where(x => x.Id == row.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.CreatedDate, (DateTime?)null)
                .SetProperty(x => x.ModifiedDate, (DateTime?)null)));
        await database.Entry(row).ReloadAsync();
        var expected = new FileFormatResponse(row.Id, null, null, null, null);
        Assert.Null(row.CreatedDate);
        Assert.Null(row.ModifiedDate);
        using var reader = app.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        foreach (var path in new[] { $"/orders/fileformats/{row.Id}", "/orders/fileformats" })
        {
            using var response = await reader.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var item = wire.RootElement.ValueKind == JsonValueKind.Array
                ? Assert.Single(wire.RootElement.EnumerateArray()) : wire.RootElement;
            AssertSourceWire(item, expected);
        }
        Assert.Equal(expected, await database.FileFormats.AsNoTracking()
            .Select(x => new FileFormatResponse(x.Id, x.Name, x.Extension, x.CreatedDate, x.ModifiedDate)).SingleAsync());
    }

    [Fact]
    public async Task ServedSchemaUsesResponseNamesAndOptionalNullsWhileRequestKeepsPascalCase()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var schemas = document["components"]!["schemas"]!.AsObject();
        var output = schemas["FileFormatResponse"]!.AsObject();
        Assert.Equal(new[] { "createdDate", "extension", "id", "modifiedDate", "name" },
            output["properties"]!.AsObject().Select(property => property.Key).Order(StringComparer.Ordinal));
        var required = output["required"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray() ?? [];
        Assert.Contains("id", required);
        foreach (var optional in new[] { "name", "extension", "createdDate", "modifiedDate" })
            Assert.DoesNotContain(optional, required);
        Assert.Equal(new[] { "Extension", "Name" },
            schemas["UpsertFileFormatRequest"]!["properties"]!.AsObject()
                .Select(property => property.Key).Order(StringComparer.Ordinal));
        Assert.True(schemas["CategoryReadResponse"]!["properties"]!.AsObject().ContainsKey("id"));
    }

    private static JsonSerializerOptions ResponseOptions() => new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static void AssertSourceWire(JsonElement actual, FileFormatResponse expected)
    {
        var source = new MvcNewtonsoftJsonOptions();
        source.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        source.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var original = Newtonsoft.Json.JsonConvert.SerializeObject(expected, source.SerializerSettings);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(actual.GetRawText())));
        var names = new List<string> { "id" };
        if (expected.Name is not null) names.Add("name");
        if (expected.Extension is not null) names.Add("extension");
        if (expected.CreatedDate is not null) names.Add("createdDate");
        if (expected.ModifiedDate is not null) names.Add("modifiedDate");
        Assert.Equal(names.Order(StringComparer.Ordinal),
            actual.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private sealed record WebFileFormatConsumer(int Id, string? Name, string? Extension);
}
