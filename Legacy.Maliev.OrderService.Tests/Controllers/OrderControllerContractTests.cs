using System.Reflection;
using System.Security.Claims;
using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderControllerContractTests
{
    public static TheoryData<Type, string> Controllers => new() { { typeof(OrdersController), "[controller]" }, { typeof(CategoriesController), "orders/[controller]" }, { typeof(FileFormatsController), "orders/[controller]" }, { typeof(FilesController), "orders/[controller]" }, { typeof(ProcessesController), "orders/[controller]" }, { typeof(OrderStatusesController), "[controller]" }, { typeof(AvailableStatusesController), "orderstatuses/[controller]" }, { typeof(HistoriesController), "orderstatuses/[controller]" } };
    [Theory, MemberData(nameof(Controllers))] public void Controllers_PreserveRoutesAndRequireAuthentication(Type t, string route) { Assert.Equal(route, t.GetCustomAttribute<RouteAttribute>()?.Template); Assert.NotNull(t.GetCustomAttribute<AuthorizeAttribute>()); }
    [Fact] public void Controllers_PreserveFiftyEightActionsAndTemplates() { var m = Controllers.SelectMany(x => ((Type)x[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray(); Assert.Equal(58, m.Length); Assert.Equal(58, m.SelectMany(x => x.GetCustomAttributes<HttpMethodAttribute>()).Count()); Assert.All(m, x => Assert.Single(x.GetCustomAttributes<RequirePermissionAttribute>())); }
    [Fact]
    public void SignedPermissionClaims_AreAuthoritativeWithoutForcedLiveIamChecks()
    {
        var methods = Controllers.SelectMany(value =>
            ((Type)value[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly));
        Assert.All(methods, method =>
        {
            var authorization = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.False(authorization.RequireLiveCheck);
        });
    }
    [Fact]
    public void CustomerOrderBoundary_HasDedicatedLeastPrivilegeRoutes()
    {
        AssertRouteAndPermission("GetCustomerOrdersAsync", "customers/{customerId:int}", "legacy.customer-orders.read");
        AssertRouteAndPermission("GetCustomerOrderAsync", "customers/{customerId:int}/{id:int}", "legacy.customer-orders.read");
        AssertRouteAndPermission("CancelCustomerOrderAsync", "customers/{customerId:int}/{id:int}/cancel", "legacy.customer-orders.cancel");
        Assert.Null(Assert.Single(typeof(OrdersController).GetMethod(nameof(OrdersController.GetPaginatedOrderAsync))!.GetCustomAttributes<HttpGetAttribute>()).Template);
    }
    [Theory][InlineData(nameof(HistoriesController.CreateOrderHistoryAcceptedStatusAsync), "{orderId:int}/accepted")][InlineData(nameof(HistoriesController.CreateOrderHistoryInProgressStatusAsync), "{orderId:int}/InProgress")][InlineData(nameof(HistoriesController.CreateOrderHistoryShippedStatusAsync), "{orderId:int}/shipped")] public void NamedStatusShortcuts_PreserveLegacyTemplates(string name, string route) => Assert.Equal(route, Assert.Single(typeof(HistoriesController).GetMethod(name)!.GetCustomAttributes<HttpPostAttribute>()).Template);

    [Fact]
    public async Task StatusIdempotency_IsCachedOnlyAfterRepositoryConvergenceSucceeds()
    {
        var service = new Mock<IOrderService>();
        service.SetupSequence(value => value.TransitionAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResult.Conflict)
            .ReturnsAsync(UpdateResult.Updated);
        var idempotency = new RecordingIdempotencyStore();
        var controller = WithPrincipal(new HistoriesController(service.Object, idempotency), "employee-a");

        var failed = await controller.CreateOrderStatusEntryAsync(42, 7, "accepted-key", default);
        Assert.IsType<ConflictObjectResult>(failed);
        Assert.Equal(0, idempotency.SetCount);

        var retried = await controller.CreateOrderStatusEntryAsync(42, 7, "accepted-key", default);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(retried).StatusCode);
        Assert.Equal(1, idempotency.SetCount);
    }

    [Fact]
    public async Task CreateOrderIdempotency_RejectsSameKeyForDifferentPayload()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"));
        var controller = WithPrincipal(new OrdersController(service.Object, new MemoryIdempotencyStore()), "employee-a");

        var created = await controller.CreateOrderAsync(Request("First"), "shared-key", default);
        var conflict = await controller.CreateOrderAsync(Request("Changed"), "shared-key", default);

        Assert.IsType<CreatedAtRouteResult>(created);
        Assert.IsType<ConflictObjectResult>(conflict);
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrderIdempotency_IsolatesSameKeyAcrossPrincipals()
    {
        var service = new Mock<IOrderService>();
        service.SetupSequence(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"))
            .ReturnsAsync(Order(2, "Second"));
        var idempotency = new MemoryIdempotencyStore();
        var firstController = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");
        var secondController = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-b");

        var first = Assert.IsType<CreatedAtRouteResult>(
            await firstController.CreateOrderAsync(Request("Same"), "shared-key", default));
        var second = Assert.IsType<CreatedAtRouteResult>(
            await secondController.CreateOrderAsync(Request("Same"), "shared-key", default));

        Assert.Equal(1, Assert.IsType<OrderResponse>(first.Value).Id);
        Assert.Equal(2, Assert.IsType<OrderResponse>(second.Value).Id);
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task StatusIdempotency_RejectsSameKeyForDifferentOrderAndStatus()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.TransitionAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResult.Updated);
        var controller = WithPrincipal(new HistoriesController(service.Object, new MemoryIdempotencyStore()), "employee-a");

        var created = await controller.CreateOrderStatusEntryAsync(42, 7, "shared-key", default);
        var conflict = await controller.CreateOrderStatusEntryAsync(43, 8, "shared-key", default);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(created).StatusCode);
        Assert.IsType<ConflictObjectResult>(conflict);
        service.Verify(
            value => value.TransitionAsync(43, 8, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateOrderIdempotency_ExactReplayReturnsOriginalResponse()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"));
        var controller = WithPrincipal(new OrdersController(service.Object, new MemoryIdempotencyStore()), "employee-a");

        var first = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), "replay-key", default));
        var replay = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), "replay-key", default));

        Assert.Same(first.Value, replay.Value);
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task StatusIdempotency_ExactReplayDoesNotRepeatTransition()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.TransitionAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(UpdateResult.Updated);
        var controller = WithPrincipal(new HistoriesController(service.Object, new MemoryIdempotencyStore()), "employee-a");

        var first = await controller.CreateOrderStatusEntryAsync(42, 7, "replay-key", default);
        var replay = await controller.CreateOrderStatusEntryAsync(42, 7, "replay-key", default);

        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(first).StatusCode);
        Assert.Equal(StatusCodes.Status201Created, Assert.IsType<StatusCodeResult>(replay).StatusCode);
        service.Verify(
            value => value.TransitionAsync(42, 7, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrderWithoutIdempotencyKey_PreservesUncachedBehavior()
    {
        var service = new Mock<IOrderService>();
        service.SetupSequence(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"))
            .ReturnsAsync(Order(2, "Second"));
        var controller = new OrdersController(service.Object, new MemoryIdempotencyStore());

        var first = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), null, default));
        var second = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), null, default));

        Assert.Equal(1, Assert.IsType<OrderResponse>(first.Value).Id);
        Assert.Equal(2, Assert.IsType<OrderResponse>(second.Value).Id);
    }

    [Fact]
    public async Task CreateOrderIdempotency_SerializedEnvelopeRoundTrips()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"));
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var idempotency = new DistributedOrderCache(distributed, NullLogger<DistributedOrderCache>.Instance);
        var controller = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");

        var first = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), "serialized-key", default));
        var replay = Assert.IsType<CreatedAtRouteResult>(
            await controller.CreateOrderAsync(Request("Same"), "serialized-key", default));

        Assert.Equal(
            Assert.IsType<OrderResponse>(first.Value),
            Assert.IsType<OrderResponse>(replay.Value));
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrderIdempotency_ConcurrentReplayExecutesOnlyOneWrite()
    {
        var service = new Mock<IOrderService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var call = Interlocked.Increment(ref calls);
                entered.TrySetResult();
                await release.Task;
                return Order(call, $"Order {call}");
            });
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var idempotency = new DistributedOrderCache(distributed, NullLogger<DistributedOrderCache>.Instance);
        var firstController = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");
        var secondController = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");

        var firstTask = firstController.CreateOrderAsync(Request("Same"), "concurrent-key", default);
        await entered.Task;
        var secondTask = secondController.CreateOrderAsync(Request("Same"), "concurrent-key", default);
        var completed = await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(1)));
        release.TrySetResult();
        var first = await firstTask;
        var second = await secondTask;

        Assert.Same(secondTask, completed);
        Assert.IsType<CreatedAtRouteResult>(first);
        Assert.IsType<ConflictObjectResult>(second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CreateOrderIdempotency_UnavailableStoreFailsClosed()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"));
        var idempotency = new DistributedOrderCache(
            new ThrowingDistributedCache(),
            NullLogger<DistributedOrderCache>.Instance);
        var controller = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");

        var result = Assert.IsType<ObjectResult>(
            await controller.CreateOrderAsync(Request("Same"), "unavailable-key", default));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateOrderIdempotency_CompletionFailureRemainsPendingForFullReplayWindow()
    {
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Order(1, "First"));
        var distributed = new CompletionFailingDistributedCache();
        var idempotency = new DistributedOrderCache(distributed, NullLogger<DistributedOrderCache>.Instance);
        var controller = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");

        var unavailable = Assert.IsType<ObjectResult>(
            await controller.CreateOrderAsync(Request("Same"), "unknown-outcome-key", default));
        var retry = await controller.CreateOrderAsync(Request("Same"), "unknown-outcome-key", default);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.IsType<ConflictObjectResult>(retry);
        Assert.True(distributed.PendingLifetime >= TimeSpan.FromHours(24));
        service.Verify(
            value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateOrderIdempotency_CancelledDomainFailureUsesIndependentReservationCleanup()
    {
        var cancellation = new CancellationTokenSource();
        var calls = 0;
        var service = new Mock<IOrderService>();
        service.Setup(value => value.CreateOrderAsync(It.IsAny<UpsertOrderRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await cancellation.CancelAsync();
                    throw new InvalidOperationException("simulated domain failure");
                }

                return Order(2, "Second");
            });
        var distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var idempotency = new DistributedOrderCache(distributed, NullLogger<DistributedOrderCache>.Instance);
        var controller = WithPrincipal(new OrdersController(service.Object, idempotency), "employee-a");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.CreateOrderAsync(Request("Same"), "cancelled-key", cancellation.Token));
        var retry = await controller.CreateOrderAsync(Request("Same"), "cancelled-key", default);

        Assert.Equal("simulated domain failure", failure.Message);
        Assert.IsType<CreatedAtRouteResult>(retry);
        Assert.Equal(2, calls);
    }

    private static void AssertRouteAndPermission(string methodName, string route, string permission)
    {
        var method = typeof(OrdersController).GetMethod(methodName);
        Assert.NotNull(method);
        Assert.Equal(route, Assert.Single(method.GetCustomAttributes<HttpMethodAttribute>()).Template);
        var authorization = Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(permission, authorization.Permission);
        Assert.False(authorization.RequireLiveCheck);
    }

    private static T WithPrincipal<T>(T controller, string subject) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "test")),
            },
        };
        return controller;
    }

    private static UpsertOrderRequest Request(string name) => new(
        CustomerId: 42,
        EmployeeId: null,
        Name: name,
        Description: "Idempotency contract",
        ProcessId: 1,
        MaterialId: null,
        SurfaceFinishId: null,
        ColorId: null,
        Quantity: 1,
        Manufactured: 0,
        UnitPrice: 100m,
        DiscountPercent: 0m,
        CurrencyId: 764,
        LeadTime: 3,
        PromisedDate: new DateTime(2026, 7, 20),
        FinishedDate: null,
        Comment: null,
        AllowSocialMedia: false,
        AllowCancellation: true,
        AllowPayment: true,
        TrackingNumber: null);

    private static OrderResponse Order(int id, string name) => new(
        Id: id,
        CustomerId: 42,
        EmployeeId: null,
        Name: name,
        Description: "Idempotency contract",
        ProcessId: 1,
        MaterialId: null,
        SurfaceFinishId: null,
        ColorId: null,
        Quantity: 1,
        Manufactured: 0,
        Remaining: 1,
        UnitPrice: 100m,
        DiscountPercent: 0m,
        Subtotal: 100m,
        CurrencyId: 764,
        LeadTime: 3,
        PromisedDate: new DateTime(2026, 7, 20),
        FinishedDate: null,
        Turnaround: null,
        Comment: null,
        AllowSocialMedia: false,
        AllowCancellation: true,
        AllowPayment: true,
        TrackingNumber: null,
        CreatedDate: new DateTime(2026, 7, 17),
        ModifiedDate: new DateTime(2026, 7, 17));

    private sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<(string Scope, string Key), Entry> values = [];

        public Task<IdempotencyAcquireResult<T>> AcquireAsync<T>(string scope, string key, string requestFingerprint, CancellationToken cancellationToken) where T : class
        {
            lock (values)
            {
                if (!values.TryGetValue((scope, key), out var entry))
                {
                    var reservationId = Guid.NewGuid().ToString("N");
                    values[(scope, key)] = new(requestFingerprint, reservationId, null);
                    return Task.FromResult(new IdempotencyAcquireResult<T>(IdempotencyAcquireState.Acquired, reservationId, null));
                }

                if (!string.Equals(entry.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
                {
                    return Task.FromResult(new IdempotencyAcquireResult<T>(IdempotencyAcquireState.Conflict, null, null));
                }

                return Task.FromResult(entry.Response is null
                    ? new IdempotencyAcquireResult<T>(IdempotencyAcquireState.InProgress, null, null)
                    : new IdempotencyAcquireResult<T>(IdempotencyAcquireState.Replay, null, (T)entry.Response));
            }
        }

        public Task CompleteAsync<T>(string scope, string key, string requestFingerprint, string reservationId, T response, CancellationToken cancellationToken) where T : class
        {
            lock (values)
            {
                var entry = values[(scope, key)];
                Assert.Equal(requestFingerprint, entry.RequestFingerprint);
                Assert.Equal(reservationId, entry.ReservationId);
                values[(scope, key)] = entry with { Response = response };
            }

            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string scope, string key, string reservationId, CancellationToken cancellationToken)
        {
            lock (values)
            {
                if (values.TryGetValue((scope, key), out var entry) && entry.ReservationId == reservationId)
                {
                    values.Remove((scope, key));
                }
            }

            return Task.CompletedTask;
        }

        private sealed record Entry(string RequestFingerprint, string ReservationId, object? Response);
    }

    private sealed class ThrowingDistributedCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw Unavailable();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw Unavailable();
        public void Refresh(string key) => throw Unavailable();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw Unavailable();
        public void Remove(string key) => throw Unavailable();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw Unavailable();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw Unavailable();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw Unavailable();
        private static InvalidOperationException Unavailable() => new("simulated idempotency store outage");
    }

    private sealed class CompletionFailingDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> values = [];
        private int sets;
        public TimeSpan PendingLifetime { get; private set; }
        public byte[]? Get(string key) => values.GetValueOrDefault(key);
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) => values.Remove(key);
        public Task RemoveAsync(string key, CancellationToken token = default) { Remove(key); return Task.CompletedTask; }
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            if (Interlocked.Increment(ref sets) > 1) throw new InvalidOperationException("simulated completion failure");
            PendingLifetime = options.AbsoluteExpirationRelativeToNow ?? TimeSpan.Zero;
            values[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingIdempotencyStore : IIdempotencyStore
    {
        public int SetCount { get; private set; }

        public Task<IdempotencyAcquireResult<T>> AcquireAsync<T>(string scope, string key, string requestFingerprint, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(new IdempotencyAcquireResult<T>(IdempotencyAcquireState.Acquired, "reservation", null));

        public Task CompleteAsync<T>(string scope, string key, string requestFingerprint, string reservationId, T response, CancellationToken cancellationToken) where T : class
        {
            SetCount++;
            return Task.CompletedTask;
        }

        public Task ReleaseAsync(string scope, string key, string reservationId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
