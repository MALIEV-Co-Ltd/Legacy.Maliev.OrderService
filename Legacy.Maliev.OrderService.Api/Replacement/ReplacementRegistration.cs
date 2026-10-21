using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Replacement;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Data.Replacement;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.OrderService.Api.Replacement;

public sealed class ReplacementOptions { public bool Enabled { get; set; } }
public static class ReplacementRegistration
{
    public static IServiceCollection AddReplacementCases(this IServiceCollection services)
    {
        services.AddOptions<ReplacementOptions>().BindConfiguration("ReplacementCases");
        services.TryAddScoped<IReplacementAuthority>(_ => new UnavailableAuthority());
        services.TryAddScoped<IReplacementEvidenceVerifier>(_ => new UnavailableEvidence());
        services.AddScoped<IReplacementRepository>(p =>
        {
            var context = p.GetRequiredService<OrderDbContext>();
            var options = (DbContextOptions<OrderDbContext>)context.GetService<IDbContextOptions>();
            return new ReplacementStore(() => new OrderDbContext(options));
        });
        services.AddScoped(p => new ReplacementService(p.GetRequiredService<IReplacementRepository>(), p.GetRequiredService<IReplacementAuthority>(),
            p.GetRequiredService<IReplacementEvidenceVerifier>(), new(p.GetRequiredService<IOptions<ReplacementOptions>>().Value.Enabled), p.GetRequiredService<TimeProvider>()));
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => ConfigureWire(o.JsonSerializerOptions));
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(o => ConfigureWire(o.SerializerOptions));
        return services;
    }
    public static void ConfigureWire(JsonSerializerOptions options)
    {
        options.AllowOutOfOrderMetadataProperties = true;
        options.Converters.Add(new JsonStringEnumConverter<ReplacementReason>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ReplacementState>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ReturnDecision>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<RecoveryFactKind>(allowIntegerValues: false));
        options.Converters.Add(new JsonStringEnumConverter<ShipmentOutcome>(allowIntegerValues: false));
    }
    // Accepted owner adapters replace these through reviewed composition. A config flag or token
    // display claims can never manufacture staff/tenant authority or verified File evidence.
    private sealed class UnavailableAuthority : IReplacementAuthority
    {
        public Task<ReplacementAuthorityDecision> AuthorizeAsync(string s, int customer, string permission, string path, CancellationToken c)
        { c.ThrowIfCancellationRequested(); return Task.FromResult(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Unavailable)); }
    }
    private sealed class UnavailableEvidence : IReplacementEvidenceVerifier
    {
        public Task RequireAsync(int customer, IReadOnlyList<int> ids, ReplacementEvidence reference, string kind, CancellationToken c)
        { c.ThrowIfCancellationRequested(); return Task.FromException(new ReplacementUnavailableException()); }
    }
}
