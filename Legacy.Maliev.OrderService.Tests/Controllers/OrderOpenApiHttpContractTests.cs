using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

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
        var probes = new[] { "/order/aspire-liveness", "/order/liveness" };
        foreach (var probe in probes)
            Assert.NotNull(Path(document, probe)["get"]);
        var operations = document["paths"]!.AsObject()
            .Where(path => !probes.Contains(path.Key, StringComparer.OrdinalIgnoreCase))
            .SelectMany(path => path.Value!.AsObject())
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
            Assert.NotNull(parameter);
            Assert.Equal("query", parameter!["in"]!.GetValue<string>());
            Assert.False(string.IsNullOrWhiteSpace(parameter["description"]?.GetValue<string>()));
            Assert.NotNull(parameter["example"]);
        }
        Assert.Equal("The owned order snapshot.", Path(document, "/orders/{id}")["get"]!["responses"]!["200"]?["description"]?.GetValue<string>());
        Assert.Equal("The order does not exist.", Path(document, "/orders/{id}")["get"]!["responses"]!["404"]?["description"]?.GetValue<string>());
        Assert.Equal("Bucket or object name is blank.", Path(document, "/orders/{orderId}/files")["post"]!["responses"]!["400"]?["description"]?.GetValue<string>());
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
            Assert.True(input.ContainsKey(name), $"Missing request scalar {name}; actual fields=[{string.Join(',', input.Select(field => field.Key))}].");
        foreach (var name in new[] { "Remaining", "Subtotal", "Turnaround", "CreatedDate", "ModifiedDate", "OrderFiles", "Process" })
            Assert.False(input.ContainsKey(name), $"Unexpected writable field {name}.");
        Assert.False(input.ContainsKey("customerId"));
        Assert.Equal("Writable order fields; computed values and server timestamps are not accepted.", request["description"]?.GetValue<string>());
        var response = Resolve(document, Path(document, "/orders/{id}")["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!.AsObject());
        var output = response["properties"]!.AsObject();
        Assert.Equal("An order snapshot with scalar service references and persisted computed values.", response["description"]?.GetValue<string>());
        foreach (var name in new[] { "Id", "Remaining", "Subtotal", "Turnaround", "CreatedDate", "ModifiedDate" })
            Assert.True(output.ContainsKey(name), $"Missing response field {name}.");
        await using var app = await fixture.AppAsync(null);
        int orderId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            var order = new Order
            {
                CustomerId = 42,
                EmployeeId = 43,
                Name = "Schema wire control",
                Process = new Process { Name = "Schema process", Category = new Category { Name = "Schema category" } },
                Quantity = 3,
                Manufactured = 1,
                UnitPrice = 12.34m,
                DiscountPercent = 0m,
            };
            database.Orders.Add(order);
            await database.SaveChangesAsync();
            orderId = order.Id;
        }
        using var reader = app.CreateClient();
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token("legacy.orders.read"));
        using var actual = await reader.GetAsync($"/orders/{orderId}");
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        var wire = JsonNode.Parse(await actual.Content.ReadAsStringAsync(),
            new JsonNodeOptions { PropertyNameCaseInsensitive = false })!.AsObject();
        Assert.Equal(42, wire["CustomerId"]!.GetValue<int>());
        Assert.Equal(2, wire["Remaining"]!.GetValue<int>());
        Assert.Equal(37.02m, wire["Subtotal"]!.GetValue<decimal>());
        Assert.False(wire.ContainsKey("customerId"));
        Assert.False(wire.ContainsKey("Turnaround"));
        Assert.All(wire, field => Assert.True(output.ContainsKey(field.Key), $"Wire field {field.Key} is absent from the response schema."));
        var metadata = Resolve(document, Path(document, "/orders/files/{id}")["put"]!["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject());
        Assert.Equal("Bucket and object metadata for an owned order; no object upload is performed.", metadata["description"]?.GetValue<string>());
        Assert.Equal(new[] { "Bucket", "ObjectName", "OrderId" }, metadata["properties"]!.AsObject().Select(field => field.Key).Order().ToArray());
        var example = metadata["example"] ?? metadata["examples"]?.AsArray().FirstOrDefault();
        Assert.NotNull(example);
        Assert.Equal(42, example["OrderId"]!.GetValue<int>());
        Assert.Equal("metadata-bucket", example["Bucket"]!.GetValue<string>());
        Assert.Equal("drawings/part.step", example["ObjectName"]!.GetValue<string>());
        Assert.Equal("Reads one order including persisted computed totals and calendar turnaround.", Path(document, "/orders/{id}")["get"]?["summary"]?.GetValue<string>());
        Assert.Equal("Creates bucket and object metadata for an existing order without uploading an object.", Path(document, "/orders/{orderId}/files")["post"]?["summary"]?.GetValue<string>());
    }
}
