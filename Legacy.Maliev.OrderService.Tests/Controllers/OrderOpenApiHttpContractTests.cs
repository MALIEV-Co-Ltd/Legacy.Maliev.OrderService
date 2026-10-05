using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderOpenApiHttpContractTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private async Task<JsonObject> DocumentAsync()
    {
        await using var app = await fixture.AppAsync(null);
        await using var development = app.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/order/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(),
            new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
    }

    private static JsonObject Path(JsonObject document, string expected) =>
        Assert.Single(document["paths"]!.AsObject(),
            path => string.Equals(path.Key, expected, StringComparison.OrdinalIgnoreCase)).Value!.AsObject();

    private static JsonObject Resolve(JsonObject document, JsonObject schema)
    {
        while (schema["$ref"] is JsonValue reference)
        {
            var value = reference.GetValue<string>();
            Assert.StartsWith("#/components/schemas/", value);
            schema = document["components"]!["schemas"]![value["#/components/schemas/".Length..]]!.AsObject();
        }
        return schema;
    }

    [Fact]
    public async Task Production_DocumentationIsUnavailableAndOrderReadRequiresAuthentication()
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        using var document = await client.GetAsync("/order/openapi/v1.json");
        using var order = await client.GetAsync("/orders/1");
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, order.StatusCode);
    }

    [Fact]
    public async Task Development_DocumentPreservesOperationCountQueriesAndConcurrencyHeaders()
    {
        var document = await DocumentAsync();
        Assert.StartsWith("3.", document["openapi"]!.GetValue<string>());
        Assert.Equal("Legacy MALIEV Order Service API", document["info"]!["title"]!.GetValue<string>());
        var operations = document["paths"]!.AsObject().SelectMany(path => path.Value!.AsObject())
            .Where(entry => entry.Key is "get" or "post" or "put" or "delete" or "patch").ToArray();
        Assert.Equal(58, operations.Length);
        Assert.All(operations, operation => Assert.NotEmpty(operation.Value!["responses"]!.AsObject()));
        foreach (var name in new[] { "sort", "search", "index", "size" })
        {
            var parameter = Assert.Single(Path(document, "/orders")["get"]!["parameters"]!.AsArray(),
                item => item!["name"]!.GetValue<string>() == name);
            Assert.Equal("query", parameter!["in"]!.GetValue<string>());
        }
        foreach (var name in new[] { "bucket", "objectName" })
        {
            var parameter = Assert.Single(Path(document, "/orders/{orderId}/files")["post"]!["parameters"]!.AsArray(),
                item => item!["name"]!.GetValue<string>() == name);
            Assert.Equal("query", parameter!["in"]!.GetValue<string>());
        }
        foreach (var (path, method, name) in new[]
        {
            ("/orders", "post", "Idempotency-Key"),
            ("/orders/{id}", "put", "X-Expected-Modified-Date"),
            ("/orderstatuses/histories/{historyId}", "put", "X-Expected-Modified-Date"),
        })
        {
            var parameter = Assert.Single(Path(document, path)[method]!["parameters"]!.AsArray(),
                item => item!["name"]!.GetValue<string>() == name);
            Assert.Equal("header", parameter!["in"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task Development_SchemasPreservePascalCaseScalarReferencesAndComputedReadOnlyFields()
    {
        var document = await DocumentAsync();
        var request = Resolve(document, Path(document, "/orders")["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject());
        var input = request["properties"]!.AsObject();
        foreach (var name in new[] { "CustomerId", "EmployeeId", "ProcessId", "MaterialId", "CurrencyId", "Quantity", "UnitPrice", "OperationKey" })
            Assert.True(input.ContainsKey(name), $"Missing request scalar {name}.");
        foreach (var name in new[] { "Remaining", "Subtotal", "Turnaround", "CreatedDate", "ModifiedDate", "OrderFiles", "Process" })
            Assert.False(input.ContainsKey(name), $"Unexpected writable field {name}.");
        Assert.False(input.ContainsKey("customerId"));
        var response = Resolve(document, Path(document, "/orders/{id}")["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!.AsObject());
        var output = response["properties"]!.AsObject();
        foreach (var name in new[] { "Id", "Remaining", "Subtotal", "Turnaround", "CreatedDate", "ModifiedDate" })
            Assert.True(output.ContainsKey(name), $"Missing response field {name}.");
        var metadata = Resolve(document, Path(document, "/orders/files/{id}")["put"]!["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject());
        Assert.Equal(new[] { "Bucket", "ObjectName", "OrderId" }, metadata["properties"]!.AsObject().Select(field => field.Key).Order().ToArray());
    }
}
