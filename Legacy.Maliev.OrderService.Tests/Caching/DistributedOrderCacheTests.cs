using System.Security.Cryptography;
using System.Text;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
namespace Legacy.Maliev.OrderService.Tests.Caching;

public sealed class DistributedOrderCacheTests
{
    [Fact]
    public async Task Idempotency_HashesPrincipalScopeAndClientKeyWithVersionedIdentity()
    {
        IDistributedCache distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IIdempotencyStore store = new DistributedOrderCache(distributed, NullLogger<DistributedOrderCache>.Instance);
        var response = new OrderStatusResponse(1, "New", null, null, null);

        var acquired = await store.AcquireAsync<OrderStatusResponse>(
            "order-status:employee-a",
            "external-key",
            "request-fingerprint",
            default);
        Assert.Equal(IdempotencyAcquireState.Acquired, acquired.State);
        await store.CompleteAsync(
            "order-status:employee-a",
            "external-key",
            "request-fingerprint",
            Assert.IsType<string>(acquired.ReservationId),
            response,
            default);
        var replay = await store.AcquireAsync<OrderStatusResponse>(
            "order-status:employee-a",
            "external-key",
            "request-fingerprint",
            default);

        Assert.Equal(IdempotencyAcquireState.Replay, replay.State);
        Assert.Equal(response, replay.Response);
        var legacyClientHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("external-key")));
        Assert.Null(await distributed.GetAsync($"idempotency:order-status:employee-a:{legacyClientHash}"));
        var legacyV2Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("order-status:employee-a\nexternal-key")));
        Assert.Null(await distributed.GetAsync($"idempotency:v2:{legacyV2Hash}"));
        Assert.Null(await distributed.GetAsync("idempotency:v3:order-status:employee-a:external-key"));
    }

    [Fact]
    public async Task GetAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedOrderCache(distributed.Object, NullLogger<DistributedOrderCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.GetAsync<OrderStatusResponse>("key", cancellationToken));
    }

    [Fact]
    public async Task SetAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedOrderCache(distributed.Object, NullLogger<DistributedOrderCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.SetAsync("key", new OrderStatusResponse(1, "New", null, null, null), TimeSpan.FromMinutes(1), cancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedOrderCache(distributed.Object, NullLogger<DistributedOrderCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.RemoveAsync("key", cancellationToken));
    }
}
