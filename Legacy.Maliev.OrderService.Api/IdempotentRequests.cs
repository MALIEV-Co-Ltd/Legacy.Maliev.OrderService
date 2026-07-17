using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Legacy.Maliev.OrderService.Application.Interfaces;

namespace Legacy.Maliev.OrderService.Api;

internal static class IdempotentRequests
{
    private static readonly JsonSerializerOptions FingerprintOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IdempotencyLookup<TResponse>> LookupAsync<TRequest, TResponse>(
        IIdempotencyStore store,
        ClaimsPrincipal principal,
        string operation,
        string? key,
        TRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new(null, null, false, false);
        }

        var principalId = principal.FindFirst("user_id")?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(principalId))
        {
            throw new InvalidOperationException("An authenticated principal identifier is required for idempotent requests.");
        }

        var context = new IdempotencyContext(
            $"{operation}:{principalId}",
            key,
            Fingerprint(request),
            null);
        var acquired = await store.AcquireAsync<TResponse>(
            context.Scope,
            context.Key,
            context.RequestFingerprint,
            cancellationToken);
        return acquired.State switch
        {
            IdempotencyAcquireState.Acquired => new(context with { ReservationId = acquired.ReservationId }, null, false, false),
            IdempotencyAcquireState.Replay => new(context, acquired.Response, false, false),
            IdempotencyAcquireState.Conflict => new(context, null, true, false),
            IdempotencyAcquireState.InProgress => new(context, null, false, true),
            _ => throw new InvalidOperationException("Unknown idempotency acquisition state."),
        };
    }

    public static Task StoreAsync<TResponse>(
        IIdempotencyStore store,
        IdempotencyContext? context,
        TResponse response,
        CancellationToken cancellationToken)
        where TResponse : class =>
        context is null
            ? Task.CompletedTask
            : store.CompleteAsync(
                context.Scope,
                context.Key,
                context.RequestFingerprint,
                context.ReservationId ?? throw new InvalidOperationException("Idempotency reservation is missing."),
                response,
                cancellationToken);

    public static Task ReleaseAsync(
        IIdempotencyStore store,
        IdempotencyContext? context,
        CancellationToken cancellationToken) =>
        context?.ReservationId is null
            ? Task.CompletedTask
            : store.ReleaseAsync(context.Scope, context.Key, context.ReservationId, cancellationToken);

    public static async Task ReleaseAfterFailureAsync(IIdempotencyStore store, IdempotencyContext? context)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await ReleaseAsync(store, context, cleanup.Token);
        }
        catch (Exception exception) when (exception is IdempotencyStoreUnavailableException or OperationCanceledException)
        {
            // Preserve the original domain failure. An unreleased reservation remains fail-closed for 24 hours.
        }
    }

    private static string Fingerprint<TRequest>(TRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, FingerprintOptions)));

    internal sealed record IdempotencyContext(string Scope, string Key, string RequestFingerprint, string? ReservationId);
    internal sealed record IdempotencyLookup<TResponse>(IdempotencyContext? Context, TResponse? Response, bool Conflict, bool InProgress)
        where TResponse : class;
}
