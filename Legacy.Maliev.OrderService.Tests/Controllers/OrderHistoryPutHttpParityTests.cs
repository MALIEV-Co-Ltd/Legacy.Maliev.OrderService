using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

// Actual Production Program and normal RS256/permission handler; no authentication or IAM replacement.
public sealed class OrderHistoryPutHttpParityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer orders = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer statuses = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    private WebApplicationFactory<Program> app = null!;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(orders.StartAsync(), statuses.StartAsync(), redis.StartAsync());
        await using var orderDb = Orders();
        await using var statusDb = Statuses();
        await Task.WhenAll(orderDb.Database.MigrateAsync(), statusDb.Database.MigrateAsync());
        app = new Factory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OrderDbContext"] = orders.GetConnectionString(),
            ["ConnectionStrings:OrderStatusDbContext"] = statuses.GetConnectionString(),
            ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://order-fixture.example",
            ["Jwt:Audience"] = "order-fixture",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability:RuntimeMetricsEnabled"] = "false"
        });
    }

    [Fact]
    public async Task SuccessfulPut_PersistsUpdateAndReturnsSource204WithEmptyBody()
    {
        var seed = await SeedAsync();
        using var client = app.CreateClient();
        using var response = await PutAsync(client, seed.HistoryId, seed.OrderId, seed.TargetId, "legacy.order-status.write");
        await using var db = Statuses();
        var stored = await db.History.AsNoTracking().SingleAsync(x => x.Id == seed.HistoryId);
        Assert.Equal(seed.TargetId, stored.OrderStatusId);
        Assert.NotEqual(seed.Modified, stored.ModifiedDate);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Append_StillReturns201AndAppendsHistory()
    {
        var seed = await SeedAsync();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/orderstatuses/Histories/{seed.OrderId}/{seed.TargetId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token("legacy.order-status.write"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = Statuses();
        Assert.Equal(2, await db.History.CountAsync(x => x.OrderId == seed.OrderId));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("legacy.orders.read", HttpStatusCode.Forbidden)]
    public async Task MissingOrWrongPermission_DoesNotMutate(string? permission, HttpStatusCode expected)
    {
        var seed = await SeedAsync();
        using var client = app.CreateClient();
        using var response = await PutAsync(client, seed.HistoryId, seed.OrderId, seed.TargetId, permission);
        Assert.Equal(expected, response.StatusCode);
        await using var db = Statuses();
        var stored = await db.History.AsNoTracking().SingleAsync(x => x.Id == seed.HistoryId);
        Assert.Equal(seed.InitialId, stored.OrderStatusId);
        Assert.Equal(seed.Modified, stored.ModifiedDate);
    }

    [Fact]
    public async Task MissingHistory_StillReturns404()
    {
        var seed = await SeedAsync();
        using var client = app.CreateClient();
        using var response = await PutAsync(client, int.MaxValue, seed.OrderId, seed.TargetId, "legacy.order-status.write");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StaleExpectedVersion_StillReturns409WithoutMutation()
    {
        var seed = await SeedAsync();
        using var client = app.CreateClient();
        using var response = await PutAsync(client, seed.HistoryId, seed.OrderId, seed.TargetId, "legacy.order-status.write", "2000-01-01T00:00:00Z");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = Statuses();
        var stored = await db.History.AsNoTracking().SingleAsync(x => x.Id == seed.HistoryId);
        Assert.Equal(seed.InitialId, stored.OrderStatusId);
        Assert.Equal(seed.Modified, stored.ModifiedDate);
    }

    private async Task<Seed> SeedAsync()
    {
        await using var orderDb = Orders();
        var order = new Order { Name = "Owned HTTP fixture", Quantity = 1, Process = new Process { Name = "Owned HTTP fixture", Category = new Category { Name = "Owned HTTP fixture" } } };
        orderDb.Add(order);
        await orderDb.SaveChangesAsync();
        await using var statusDb = Statuses();
        var initial = new OrderStatus { Name = "New" };
        var target = new OrderStatus { Name = "Quoted" };
        var modified = new DateTime(2026, 1, 1);
        var history = new OrderStatusHistory { OrderId = order.Id, OrderStatus = initial, CreatedDate = modified, ModifiedDate = modified };
        statusDb.AddRange(history, target);
        statusDb.Transitions.Add(new OrderStatusTransition { OrderStatus = initial, PossibleStatus = target });
        await statusDb.SaveChangesAsync();
        return new Seed(order.Id, history.Id, initial.Id, target.Id, modified);
    }

    private async Task<HttpResponseMessage> PutAsync(HttpClient client, int id, int orderId, int statusId, string? permission, string? expected = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/orderstatuses/Histories/{id}")
        { Content = JsonContent.Create(new { OrderId = orderId, OrderStatusId = statusId }) };
        if (permission is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(permission));
        if (expected is not null) request.Headers.Add("X-Expected-Modified-Date", expected);
        return await client.SendAsync(request);
    }

    private string Token(string permission) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "https://order-fixture.example", audience: "order-fixture",
        claims: [new Claim("sub", "employee-fixture"), new Claim("identity_kind", "employee"), new Claim("permissions", permission)],
        notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
    private OrderDbContext Orders() => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(orders.GetConnectionString()).Options);
    private OrderStatusDbContext Statuses() => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(statuses.GetConnectionString()).Options);
    private sealed record Seed(int OrderId, int HistoryId, int InitialId, int TargetId, DateTime Modified);

    public async Task DisposeAsync()
    {
        await app.DisposeAsync();
        await Task.WhenAll(orders.DisposeAsync().AsTask(), statuses.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        key.Dispose();
    }

    private sealed class Factory(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
