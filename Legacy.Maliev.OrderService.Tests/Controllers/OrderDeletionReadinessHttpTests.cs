using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderDeletionReadinessHttpTests(OrderDeletionReadinessFixture fixture) : IClassFixture<OrderDeletionReadinessFixture>
{
    [Fact]
    public async Task CompletePhysicalSchema_IsReady()
    {
        await using var app = await fixture.AppAsync(null);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/order/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task DisabledDeleteAdmission_Returns503WithoutDeletingEitherGraph()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var order = new Order { Name = "Owned admission fixture", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned.stl" });
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{order.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == order.Id));
        Assert.Single(await orders.Files.AsNoTracking().Where(x => x.OrderId == order.Id).ToListAsync());
        Assert.Single(await statuses.History.AsNoTracking().Where(x => x.OrderId == order.Id).ToListAsync());
        Assert.Empty(await orders.DeletionIntents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NormallyConfiguredRepository_DeleteFailurePreservesBothGraphs()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.True(orders.Database.CreateExecutionStrategy().RetriesOnFailure);
        var order = new Order { Name = "Owned retry fixture", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned.stl" });
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        await orders.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_owned_delete() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'owned disposable delete fault'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_owned_delete BEFORE DELETE ON "Order"
            FOR EACH ROW EXECUTE FUNCTION reject_owned_delete();
            """);
        try
        {
            var error = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<IOrderService>().DeleteOrderAsync(order.Id, default));
            Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == order.Id));
            Assert.Single(await orders.Files.AsNoTracking().Where(x => x.OrderId == order.Id).ToListAsync());
            var remaining = await statuses.History.AsNoTracking().CountAsync(x => x.OrderId == order.Id);
            Assert.True(remaining == 1, $"Expected original history preserved; observed {remaining}. Failure category: {error?.GetType().Name ?? "none"}.");
            Assert.IsType<OrderDeletionUnavailableException>(error);
        }
        finally
        {
            await orders.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_owned_delete ON \"Order\"; DROP FUNCTION reject_owned_delete();");
        }
    }

    [Theory]
    [InlineData("unexpected-default")]
    [InlineData("row-security")]
    [InlineData("forced-row-security")]
    [InlineData("insert-rule")]
    [InlineData("receipt-trigger")]
    [InlineData("disabled-receipt-trigger")]
    public async Task IntentBehaviorDrift_ReadinessAndEnabledDeleteDenyWithoutEffects(string drift)
    {
        await using var app = await fixture.AppAsync(drift, recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var order = new Order { Name = "Owned behavioral drift", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned.stl" });
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        using var client = app.CreateClient();
        using var readiness = await client.GetAsync("/order/readiness");
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/orders/{order.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var deletion = await client.SendAsync(request);
        var orderCount = await orders.Orders.AsNoTracking().CountAsync(x => x.Id == order.Id);
        var fileCount = await orders.Files.AsNoTracking().CountAsync(x => x.OrderId == order.Id);
        var historyCount = await statuses.History.AsNoTracking().CountAsync(x => x.OrderId == order.Id);
        var intentCount = await orders.DeletionIntents.AsNoTracking().CountAsync();
        Assert.True(readiness.StatusCode == HttpStatusCode.ServiceUnavailable
            && deletion.StatusCode == HttpStatusCode.ServiceUnavailable
            && orderCount == 1 && fileCount == 1 && historyCount == 1 && intentCount == 0,
            $"Drift {drift}: readiness={(int)readiness.StatusCode}, delete={(int)deletion.StatusCode}; Order={orderCount}, File={fileCount}, History={historyCount}, Intent={intentCount}. Expected 503/503 and 1/1/1/0.");
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("missing-index")]
    [InlineData("wrong-column-type")]
    [InlineData("descending-queue")]
    [InlineData("wrong-index-method")]
    [InlineData("unexpected-order-fk")]
    [InlineData("deferrable-receipt")]
    public async Task MissingOrDriftedIntentAuthority_IsNotReadyEvenWhenFeatureDisabled(string drift)
    {
        await using var app = await fixture.AppAsync(drift);
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/order/readiness");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class OrderDeletionReadinessFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    public string DeleteToken() => Token("legacy.orders.delete");
    public string Token(string permission) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "https://order-readiness-fixture.example", audience: "order-readiness-fixture",
        claims: [new Claim("sub", "employee-fixture"), new Claim("identity_kind", "employee"), new Claim("permissions", permission)],
        notBefore: DateTime.UtcNow.AddMinutes(-1), expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
    public async Task InitializeAsync() => await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        key.Dispose();
    }

    public async Task<WebApplicationFactory<Program>> AppAsync(string? drift, bool workerEnabled = false, Action<IServiceCollection>? configure = null, bool recoveryEnabled = false)
    {
        var orderConnection = await DatabaseAsync();
        var statusConnection = await DatabaseAsync();
        await using var orders = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(orderConnection).Options);
        await using var statuses = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(statusConnection).Options);
        if (drift == "previous") await orders.GetService<IMigrator>().MigrateAsync("20260829182133_AddDurableOrderOperationKey");
        else await orders.Database.MigrateAsync();
        await statuses.Database.MigrateAsync();
        if (drift == "missing-index") await orders.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_OrderDeletionIntent_PendingDue\"");
        if (drift == "wrong-column-type") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" ALTER COLUMN \"AttemptCount\" TYPE bigint");
        if (drift == "descending-queue") await orders.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_OrderDeletionIntent_PendingDue\"; CREATE INDEX \"IX_OrderDeletionIntent_PendingDue\" ON \"OrderDeletionIntent\" (\"NextAttemptAtUtc\" DESC, \"OrderId\") WHERE \"CompletedAtUtc\" IS NULL");
        if (drift == "wrong-index-method") await orders.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_OrderDeletionIntent_PendingDue\"; CREATE INDEX \"IX_OrderDeletionIntent_PendingDue\" ON \"OrderDeletionIntent\" USING brin (\"NextAttemptAtUtc\", \"OrderId\") WHERE \"CompletedAtUtc\" IS NULL");
        if (drift == "unexpected-order-fk") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" ADD CONSTRAINT \"owned_unexpected_order_fk\" FOREIGN KEY (\"OrderId\") REFERENCES \"Order\" (\"ID\")");
        if (drift == "deferrable-receipt") await orders.Database.ExecuteSqlRawAsync("DROP INDEX \"IX_OrderDeletionIntent_DeletionId\"; ALTER TABLE \"OrderDeletionIntent\" ADD CONSTRAINT \"IX_OrderDeletionIntent_DeletionId\" UNIQUE (\"DeletionId\") DEFERRABLE INITIALLY DEFERRED");
        if (drift == "unexpected-default") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" ALTER COLUMN \"AttemptCount\" SET DEFAULT 9");
        if (drift == "row-security") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" ENABLE ROW LEVEL SECURITY");
        if (drift == "forced-row-security") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" ENABLE ROW LEVEL SECURITY; ALTER TABLE \"OrderDeletionIntent\" FORCE ROW LEVEL SECURITY");
        if (drift == "insert-rule") await orders.Database.ExecuteSqlRawAsync("CREATE RULE owned_discard_intent AS ON INSERT TO \"OrderDeletionIntent\" DO INSTEAD NOTHING");
        if (drift is "receipt-trigger" or "disabled-receipt-trigger") await orders.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION owned_rewrite_receipt() RETURNS trigger AS $$
            BEGIN NEW."DeletionId" := '00000000-0000-0000-0000-000000000001'::uuid; RETURN NEW; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER owned_rewrite_receipt BEFORE INSERT ON "OrderDeletionIntent"
            FOR EACH ROW EXECUTE FUNCTION owned_rewrite_receipt();
            """);
        if (drift == "disabled-receipt-trigger") await orders.Database.ExecuteSqlRawAsync("ALTER TABLE \"OrderDeletionIntent\" DISABLE TRIGGER owned_rewrite_receipt");
        return new Factory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:OrderDbContext"] = orderConnection,
            ["ConnectionStrings:OrderStatusDbContext"] = statusConnection,
            ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://order-readiness-fixture.example",
            ["Jwt:Audience"] = "order-readiness-fixture",
            ["OrderDeletionWorker:Enabled"] = workerEnabled.ToString(),
            ["OrderDeletionRecovery:Enabled"] = recoveryEnabled.ToString(),
            ["OrderDeletionWorker:BatchSize"] = "2",
            ["OrderDeletionWorker:PollIntervalSeconds"] = "1",
            ["OrderDeletionWorker:AttemptTimeoutSeconds"] = "10",
            ["OrderDeletionWorker:MaxBackoffSeconds"] = "300",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability:RuntimeMetricsEnabled"] = "false"
        }, configure);
    }

    public WebApplicationFactory<Program> AdditionalWorkerApp(WebApplicationFactory<Program> first)
    {
        var configuration = first.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var names = new[] { "ConnectionStrings:OrderDbContext", "ConnectionStrings:OrderStatusDbContext", "ConnectionStrings:redis", "Jwt:PublicKey", "Jwt:Issuer", "Jwt:Audience", "OrderDeletionWorker:Enabled", "OrderDeletionWorker:BatchSize", "OrderDeletionWorker:PollIntervalSeconds", "OrderDeletionWorker:AttemptTimeoutSeconds", "OrderDeletionWorker:MaxBackoffSeconds", "OTEL_EXPORTER_OTLP_ENDPOINT", "Observability:RuntimeMetricsEnabled" };
        return new Factory(names.ToDictionary(x => x, x => configuration[x]));
    }

    private async Task<string> DatabaseAsync()
    {
        var name = "owned_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = name, Pooling = false }.ConnectionString;
    }

    private sealed class Factory(Dictionary<string, string?> settings, Action<IServiceCollection>? configure = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            if (configure is not null) builder.ConfigureServices(configure);
        }
    }
}
