using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderTextHttpValidationTests(OrderTextHttpFixture fixture) : IClassFixture<OrderTextHttpFixture>
{
    [Theory]
    [InlineData(101, 1, 0, "Name")]
    [InlineData(1, 251, 0, "Description")]
    [InlineData(1, 1, 129, "OperationKey")]
    public async Task CreateOrder_OversizedText_ReturnsFieldErrorWithoutPostgresWrite(
        int nameLength,
        int descriptionLength,
        int operationKeyLength,
        string field)
    {
        await using var before = fixture.CreateOrderContext();
        var originalCount = await before.Orders.CountAsync();

        using var response = await fixture.Client.PostAsJsonAsync(
            "/Orders",
            Request(nameLength, descriptionLength) with
            {
                OperationKey = operationKeyLength == 0 ? null : new string('K', operationKeyLength),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(field, problem.Errors.Keys);
        await using var after = fixture.CreateOrderContext();
        Assert.Equal(originalCount, await after.Orders.CountAsync());
    }

    [Theory]
    [InlineData(101, 1, 0, "Name")]
    [InlineData(1, 251, 0, "Description")]
    [InlineData(1, 1, 129, "OperationKey")]
    public async Task UpdateOrder_OversizedText_ReturnsFieldErrorWithoutPostgresWrite(
        int nameLength,
        int descriptionLength,
        int operationKeyLength,
        string field)
    {
        await using var before = fixture.CreateOrderContext();
        var original = await before.Orders.AsNoTracking().SingleAsync(order => order.Id == fixture.OrderId);
        var originalCount = await before.Orders.CountAsync();

        using var response = await fixture.Client.PutAsJsonAsync(
            $"/Orders/{fixture.OrderId}",
            Request(nameLength, descriptionLength) with
            {
                OperationKey = operationKeyLength == 0 ? null : new string('K', operationKeyLength),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains(field, problem.Errors.Keys);
        await using var after = fixture.CreateOrderContext();
        var persisted = await after.Orders.AsNoTracking().SingleAsync(order => order.Id == fixture.OrderId);
        Assert.Equal(originalCount, await after.Orders.CountAsync());
        Assert.Equal(original.Name, persisted.Name);
        Assert.Equal(original.Description, persisted.Description);
        Assert.Equal(original.ModifiedDate, persisted.ModifiedDate);
    }

    [Fact]
    public async Task CreateOrder_MaximumLengthText_PersistsThroughHttp()
    {
        await using var before = fixture.CreateOrderContext();
        var originalCount = await before.Orders.CountAsync();

        using var response = await fixture.Client.PostAsJsonAsync("/Orders", Request(100, 250));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var after = fixture.CreateOrderContext();
        Assert.Equal(originalCount + 1, await after.Orders.CountAsync());
        Assert.Contains(await after.Orders.AsNoTracking().ToListAsync(), order =>
            order.Name?.Length == 100 && order.Description?.Length == 250);
    }

    private static UpsertOrderRequest Request(int nameLength, int descriptionLength) => new(
        CustomerId: null,
        EmployeeId: null,
        Name: new string('N', nameLength),
        Description: new string('D', descriptionLength),
        ProcessId: 1,
        MaterialId: null,
        SurfaceFinishId: null,
        ColorId: null,
        Quantity: 1,
        Manufactured: 0,
        UnitPrice: 1m,
        DiscountPercent: 0m,
        CurrencyId: null,
        LeadTime: null,
        PromisedDate: null,
        FinishedDate: null,
        Comment: null,
        AllowSocialMedia: false,
        AllowCancellation: false,
        AllowPayment: false,
        TrackingNumber: null);
}

public sealed class OrderTextHttpFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer orders = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer statuses = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private WebApplication? app;

    public HttpClient Client { get; private set; } = null!;
    public int OrderId { get; private set; }

    public OrderDbContext CreateOrderContext() => new(
        new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(orders.GetConnectionString()).Options);

    public async Task InitializeAsync()
    {
        await Task.WhenAll(orders.StartAsync(), statuses.StartAsync());
        await using (var orderContext = CreateOrderContext())
        await using (var statusContext = new OrderStatusDbContext(
            new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(statuses.GetConnectionString()).Options))
        {
            await Task.WhenAll(orderContext.Database.MigrateAsync(), statusContext.Database.MigrateAsync());
            var category = new Category { Name = "Validation" };
            var process = new Process { Name = "Validation", Category = category };
            var order = new Order
            {
                Name = "Original",
                Description = "Unchanged",
                Process = process,
                Quantity = 1,
                Manufactured = 0,
            };
            orderContext.Orders.Add(order);
            await orderContext.SaveChangesAsync();
            OrderId = order.Id;
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly);
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
            "Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            foreach (var method in new[]
            {
                typeof(OrdersController).GetMethod(nameof(OrdersController.CreateOrderAsync))!,
                typeof(OrdersController).GetMethod(nameof(OrdersController.UpdateOrderAsync))!,
            })
            {
                var permission = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
                options.AddPolicy(permission.Policy!, new AuthorizationPolicyBuilder("Test")
                    .RequireAuthenticatedUser().Build());
            }
        });
        builder.Services.AddDbContext<OrderDbContext>(options => options.UseNpgsql(orders.GetConnectionString()));
        builder.Services.AddDbContext<OrderStatusDbContext>(options => options.UseNpgsql(statuses.GetConnectionString()));
        builder.Services.AddScoped<IOrderService, OrderRepository>();
        builder.Services.AddSingleton<IOrderCache, NoopOrderCache>();
        builder.Services.AddSingleton<IIdempotencyStore, NoopIdempotencyStore>();
        builder.Services.AddSingleton(TimeProvider.System);

        app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        Client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (app is not null) await app.DisposeAsync();
        await orders.DisposeAsync();
        await statuses.DisposeAsync();
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "order-http-validation")], "Test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
        }
    }

    private sealed class NoopOrderCache : IOrderCache
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult<T?>(null);
        public Task SetAsync<T>(string key, T value, TimeSpan duration, CancellationToken cancellationToken)
            where T : class => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoopIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyAcquireResult<T>> AcquireAsync<T>(
            string scope, string key, string requestFingerprint, CancellationToken cancellationToken) where T : class =>
            throw new InvalidOperationException("Invalid order requests must not acquire an idempotency reservation.");
        public Task CompleteAsync<T>(string scope, string key, string requestFingerprint, string reservationId,
            T response, CancellationToken cancellationToken) where T : class =>
            throw new InvalidOperationException("Invalid order requests must not complete an idempotency reservation.");
        public Task ReleaseAsync(string scope, string key, string reservationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Invalid order requests must not release an idempotency reservation.");
    }
}
