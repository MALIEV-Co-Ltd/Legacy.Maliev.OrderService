using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Legacy.Maliev.OrderService.Tests.Caching;

public sealed class RedisIdempotencyIntegrationTests : IAsyncLifetime
{
    private readonly IContainer redisContainer = new ContainerBuilder("redis:8-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
        .Build();
    private IConnectionMultiplexer? connection;

    public async Task InitializeAsync()
    {
        await redisContainer.StartAsync();
        connection = await ConnectionMultiplexer.ConnectAsync(
            $"{redisContainer.Hostname}:{redisContainer.GetMappedPublicPort(6379)},abortConnect=false");
    }

    public async Task DisposeAsync()
    {
        if (connection is not null)
        {
            await connection.CloseAsync();
            connection.Dispose();
        }

        await redisContainer.DisposeAsync();
    }

    [Fact]
    public async Task RedisReservation_AllowsOneWriterThenReplaysCompletedResponse()
    {
        var firstStore = Store();
        var secondStore = Store();

        var acquisitions = await Task.WhenAll(
            firstStore.AcquireAsync<OrderStatusResponse>("order:employee-a", "shared-key", "fingerprint", default),
            secondStore.AcquireAsync<OrderStatusResponse>("order:employee-a", "shared-key", "fingerprint", default));

        var acquired = Assert.Single(acquisitions, value => value.State == IdempotencyAcquireState.Acquired);
        Assert.Single(acquisitions, value => value.State == IdempotencyAcquireState.InProgress);
        var response = new OrderStatusResponse(7, "Accepted", null, null, null);
        await firstStore.CompleteAsync(
            "order:employee-a",
            "shared-key",
            "fingerprint",
            Assert.IsType<string>(acquired.ReservationId),
            response,
            default);

        var replay = await secondStore.AcquireAsync<OrderStatusResponse>(
            "order:employee-a",
            "shared-key",
            "fingerprint",
            default);
        var mismatch = await secondStore.AcquireAsync<OrderStatusResponse>(
            "order:employee-a",
            "shared-key",
            "different-fingerprint",
            default);

        Assert.Equal(IdempotencyAcquireState.Replay, replay.State);
        Assert.Equal(response, replay.Response);
        Assert.Equal(IdempotencyAcquireState.Conflict, mismatch.State);
    }

    private IIdempotencyStore Store() => new DistributedOrderCache(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        NullLogger<DistributedOrderCache>.Instance,
        connection ?? throw new InvalidOperationException("Redis connection is not initialized."));
}
