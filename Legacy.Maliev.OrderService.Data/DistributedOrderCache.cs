using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Legacy.Maliev.OrderService.Data;

/// <summary>Redis adapter for authorized read caching and create-response idempotency.</summary>
public sealed class DistributedOrderCache(
    IDistributedCache cache,
    ILogger<DistributedOrderCache> logger,
    IConnectionMultiplexer? redis = null) : IOrderCache, IIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim LocalIdempotencyLock = new(1, 1);
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan CompletedLifetime = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var bytes = await cache.GetAsync(key, cancellationToken);
            return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Order cache read failed; using PostgreSQL");
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = lifetime,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Order cache write failed; continuing without cache");
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Order cache invalidation failed");
        }
    }

    /// <inheritdoc />
    async Task<IdempotencyAcquireResult<T>> IIdempotencyStore.AcquireAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            return redis is null
                ? await AcquireLocalAsync<T>(scope, key, requestFingerprint, cancellationToken)
                : await AcquireRedisAsync<T>(scope, key, requestFingerprint, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Idempotency reservation failed; rejecting keyed write");
            throw new IdempotencyStoreUnavailableException("Idempotency reservation is unavailable.", exception);
        }
    }

    /// <inheritdoc />
    async Task IIdempotencyStore.CompleteAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        string reservationId,
        T response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (redis is null)
            {
                await CompleteLocalAsync(scope, key, requestFingerprint, reservationId, response, cancellationToken);
            }
            else
            {
                await CompleteRedisAsync(scope, key, requestFingerprint, reservationId, response, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Idempotency completion failed; rejecting keyed write response");
            throw new IdempotencyStoreUnavailableException("Idempotency completion is unavailable.", exception);
        }
    }

    /// <inheritdoc />
    async Task IIdempotencyStore.ReleaseAsync(
        string scope,
        string key,
        string reservationId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (redis is null)
            {
                await ReleaseLocalAsync(scope, key, reservationId, cancellationToken);
            }
            else
            {
                await ReleaseRedisAsync(scope, key, reservationId, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Idempotency reservation release failed");
            throw new IdempotencyStoreUnavailableException("Idempotency reservation release is unavailable.", exception);
        }
    }

    private async Task<IdempotencyAcquireResult<T>> AcquireLocalAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        CancellationToken cancellationToken)
        where T : class
    {
        var storageKey = IdempotencyKey(scope, key);
        await LocalIdempotencyLock.WaitAsync(cancellationToken);
        try
        {
            var existing = await cache.GetAsync(storageKey, cancellationToken);
            if (existing is not null)
            {
                return Evaluate<T>(existing, requestFingerprint);
            }

            var reservationId = Guid.NewGuid().ToString("N");
            await cache.SetAsync(
                storageKey,
                Serialize(new IdempotencyEnvelope<T>(requestFingerprint, reservationId, false, null)),
                Options(PendingLifetime),
                cancellationToken);
            return new(IdempotencyAcquireState.Acquired, reservationId, null);
        }
        finally
        {
            LocalIdempotencyLock.Release();
        }
    }

    private async Task<IdempotencyAcquireResult<T>> AcquireRedisAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        CancellationToken cancellationToken)
        where T : class
    {
        var database = redis!.GetDatabase();
        var redisKey = RedisKey(scope, key);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var reservationId = Guid.NewGuid().ToString("N");
            var pending = Serialize(new IdempotencyEnvelope<T>(requestFingerprint, reservationId, false, null));
            if (await database.StringSetAsync(redisKey, pending, PendingLifetime, When.NotExists).WaitAsync(cancellationToken))
            {
                return new(IdempotencyAcquireState.Acquired, reservationId, null);
            }

            var existing = await database.StringGetAsync(redisKey).WaitAsync(cancellationToken);
            if (existing.HasValue)
            {
                return Evaluate<T>((byte[])existing!, requestFingerprint);
            }
        }

        throw new InvalidOperationException("Idempotency reservation changed repeatedly during acquisition.");
    }

    private async Task CompleteLocalAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        string reservationId,
        T response,
        CancellationToken cancellationToken)
        where T : class
    {
        var storageKey = IdempotencyKey(scope, key);
        await LocalIdempotencyLock.WaitAsync(cancellationToken);
        try
        {
            var current = await cache.GetAsync(storageKey, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency reservation expired before completion.");
            ValidateReservation<T>(current, requestFingerprint, reservationId);
            await cache.SetAsync(
                storageKey,
                Serialize(new IdempotencyEnvelope<T>(requestFingerprint, reservationId, true, response)),
                Options(CompletedLifetime),
                cancellationToken);
        }
        finally
        {
            LocalIdempotencyLock.Release();
        }
    }

    private async Task CompleteRedisAsync<T>(
        string scope,
        string key,
        string requestFingerprint,
        string reservationId,
        T response,
        CancellationToken cancellationToken)
        where T : class
    {
        var database = redis!.GetDatabase();
        var redisKey = RedisKey(scope, key);
        var current = await database.StringGetAsync(redisKey).WaitAsync(cancellationToken);
        if (!current.HasValue)
        {
            throw new InvalidOperationException("Idempotency reservation expired before completion.");
        }

        var currentBytes = (byte[])current!;
        ValidateReservation<T>(currentBytes, requestFingerprint, reservationId);
        var transaction = database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(redisKey, current));
        _ = transaction.StringSetAsync(
            redisKey,
            Serialize(new IdempotencyEnvelope<T>(requestFingerprint, reservationId, true, response)),
            CompletedLifetime);
        if (!await transaction.ExecuteAsync().WaitAsync(cancellationToken))
        {
            throw new InvalidOperationException("Idempotency reservation changed before completion.");
        }
    }

    private async Task ReleaseLocalAsync(
        string scope,
        string key,
        string reservationId,
        CancellationToken cancellationToken)
    {
        var storageKey = IdempotencyKey(scope, key);
        await LocalIdempotencyLock.WaitAsync(cancellationToken);
        try
        {
            var current = await cache.GetAsync(storageKey, cancellationToken);
            if (current is not null && IsPendingReservation(current, reservationId))
            {
                await cache.RemoveAsync(storageKey, cancellationToken);
            }
        }
        finally
        {
            LocalIdempotencyLock.Release();
        }
    }

    private async Task ReleaseRedisAsync(string scope, string key, string reservationId, CancellationToken cancellationToken)
    {
        var database = redis!.GetDatabase();
        var redisKey = RedisKey(scope, key);
        var current = await database.StringGetAsync(redisKey).WaitAsync(cancellationToken);
        if (!current.HasValue || !IsPendingReservation((byte[])current!, reservationId))
        {
            return;
        }

        var transaction = database.CreateTransaction();
        transaction.AddCondition(Condition.StringEqual(redisKey, current));
        _ = transaction.KeyDeleteAsync(redisKey);
        _ = await transaction.ExecuteAsync().WaitAsync(cancellationToken);
    }

    private static IdempotencyAcquireResult<T> Evaluate<T>(byte[] bytes, string requestFingerprint) where T : class
    {
        var envelope = Deserialize<T>(bytes);
        if (!string.Equals(envelope.RequestFingerprint, requestFingerprint, StringComparison.Ordinal))
        {
            return new(IdempotencyAcquireState.Conflict, null, null);
        }

        if (!envelope.Completed)
        {
            return new(IdempotencyAcquireState.InProgress, null, null);
        }

        return envelope.Response is null
            ? throw new InvalidDataException("Completed idempotency entry has no response.")
            : new(IdempotencyAcquireState.Replay, null, envelope.Response);
    }

    private static void ValidateReservation<T>(byte[] bytes, string requestFingerprint, string reservationId) where T : class
    {
        var envelope = Deserialize<T>(bytes);
        if (envelope.Completed
            || !string.Equals(envelope.RequestFingerprint, requestFingerprint, StringComparison.Ordinal)
            || !string.Equals(envelope.ReservationId, reservationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Idempotency reservation ownership was lost.");
        }
    }

    private static bool IsPendingReservation(byte[] bytes, string reservationId)
    {
        var envelope = JsonSerializer.Deserialize<IdempotencyEnvelope<object>>(bytes, JsonOptions);
        return envelope is { Completed: false }
            && string.Equals(envelope.ReservationId, reservationId, StringComparison.Ordinal);
    }

    private static byte[] Serialize<T>(IdempotencyEnvelope<T> envelope) where T : class =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    private static IdempotencyEnvelope<T> Deserialize<T>(byte[] bytes) where T : class =>
        JsonSerializer.Deserialize<IdempotencyEnvelope<T>>(bytes, JsonOptions)
        ?? throw new InvalidDataException("Idempotency entry could not be deserialized.");

    private static DistributedCacheEntryOptions Options(TimeSpan lifetime) => new()
    {
        AbsoluteExpirationRelativeToNow = lifetime,
    };

    private static string IdempotencyKey(string scope, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}\n{key}")));
        return $"idempotency:v3:{hash}";
    }

    private static string RedisKey(string scope, string key) => $"legacy:order:{IdempotencyKey(scope, key)}";
    private sealed record IdempotencyEnvelope<T>(string RequestFingerprint, string ReservationId, bool Completed, T? Response) where T : class;
}
