using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
namespace Legacy.Maliev.OrderService.Tests.Caching;

public sealed class DistributedOrderCacheTests { [Fact] public async Task Idempotency_HashesClientKeyAndRoundTripsSuccessfulResponse() { IDistributedCache d = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())); IIdempotencyStore s = new DistributedOrderCache(d, NullLogger<DistributedOrderCache>.Instance); var v = new OrderStatusResponse(1, "New", null, null, null); await s.SetAsync("order-status", "external-key", v, default); Assert.Equal(v, await s.GetAsync<OrderStatusResponse>("order-status", "external-key", default)); Assert.Null(await d.GetAsync("idempotency:order-status:external-key")); } }
