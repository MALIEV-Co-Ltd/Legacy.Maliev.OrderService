using Legacy.Maliev.OrderService.Data.Replacement;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Replacement;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

// Test HTTP policy and owner boundaries are controlled; these tests do not accept a producer or live grants.
public sealed class ReplacementHttpTests : IAsyncLifetime
{
    private WebApplication app = null!;
    private HttpClient client = null!;
    private readonly Mock<IReplacementRepository> repository = new();
    private readonly Mock<IReplacementAuthority> authority = new();
    private readonly Mock<IReplacementEvidenceVerifier> evidence = new();
    private bool enabled;
    private IReplacementRepository? durableRepository;
    private bool failCacheCompletion;
    public async Task InitializeAsync()
    {
        var b = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        b.WebHost.UseTestServer();
        b.Services.AddControllers().AddApplicationPart(typeof(OrdersController).Assembly).AddJsonOptions(o => { o.JsonSerializerOptions.PropertyNamingPolicy = null; Legacy.Maliev.OrderService.Api.Replacement.ReplacementRegistration.ConfigureWire(o.JsonSerializerOptions); });
        b.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { });
        b.Services.AddAuthorization(o =>
        {
            foreach (var permission in typeof(OrdersController).Assembly.GetTypes().SelectMany(t => t.GetMethods())
                .SelectMany(m => m.GetCustomAttributes<RequirePermissionAttribute>()).DistinctBy(x => x.Policy))
                o.AddPolicy(permission.Policy!, new AuthorizationPolicyBuilder("Test").RequireAuthenticatedUser().RequireClaim("permission", permission.Permission).Build());
        });
        b.Services.AddScoped(_ => new ReplacementService(durableRepository ?? repository.Object, authority.Object, evidence.Object, new(enabled), TimeProvider.System));
        b.Services.AddSingleton<IIdempotencyStore>(new FaultingCompletion(new DistributedOrderCache(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())), NullLogger<DistributedOrderCache>.Instance), () => failCacheCompletion));
        app = b.Build(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); await app.StartAsync(); client = app.GetTestClient();
    }
    public async Task DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    private void Staff(string? permission) { client.DefaultRequestHeaders.Add("X-Test-Subject", "staff"); if (permission is not null) client.DefaultRequestHeaders.Add("X-Test-Permission", permission); }

    [Fact]
    public async Task Lineage_route_requires_authentication()
    {
        using var response = await client.GetAsync("/replacementcases/3/lineage");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); repository.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Lineage_route_requires_explicit_permission()
    {
        Staff(null); using var response = await client.GetAsync("/replacementcases/3/lineage"); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); repository.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Default_disabled_lineage_is_unavailable_without_database_access()
    {
        Staff(ReplacementPermissions.Read); using var response = await client.GetAsync("/replacementcases/3/lineage"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); repository.VerifyNoOtherCalls(); authority.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(ReplacementAuthorityOutcome.Unavailable, 503)]
    [InlineData(ReplacementAuthorityOutcome.Denied, 403)]
    public async Task Authority_failure_has_explicit_http_semantics(ReplacementAuthorityOutcome outcome, int status)
    {
        enabled = true; Staff(ReplacementPermissions.Read); Seed();
        authority.Setup(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Read, "/replacementcases/3", It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(outcome));
        using var response = await client.GetAsync("/replacementcases/3/lineage"); Assert.Equal(status, (int)response.StatusCode);
    }
    [Fact]
    public async Task Controlled_authorized_lineage_uses_exact_pascal_case_owner_ids()
    {
        enabled = true; Staff(ReplacementPermissions.Read); Seed();
        authority.Setup(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Read, "/replacementcases/3", It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        using var response = await client.GetAsync("/replacementcases/3/lineage"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("CaseId").GetInt32()); Assert.Equal(42, body.RootElement.GetProperty("CustomerId").GetInt32());
        Assert.Equal(10, body.RootElement.GetProperty("OriginalOrderIds")[0].GetInt32()); Assert.Equal(1, body.RootElement.GetProperty("Revision").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("caseId", out _));
    }
    [Fact]
    public async Task Successful_http_replay_deserializes_cache_and_rechecks_current_authority()
    {
        enabled = true; Staff(ReplacementPermissions.Write); var value = Seed();
        authority.Setup(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Write, "/customers/42/replacementcases", It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        repository.Setup(x => x.OriginalOrderMatchesAsync(42, 10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.CreateAsync(42, ReplacementReason.CarrierDamage, It.IsAny<IReadOnlyList<ReplacementAffectedInput>>(), It.IsAny<ReplacementEvidence>(), 7, It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(value);
        var input = new ReportReplacementRequest(42, ReplacementReason.CarrierDamage, [new(10, 1)], value.Value.ReportEvidence);
        var operation = Guid.NewGuid().ToString("D");
        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/replacementcases/customers/42") { Content = JsonContent.Create(input, options: ReportWire()) };
            request.Headers.Add("Idempotency-Key", operation); return await client.SendAsync(request);
        }
        using var first = await Send(); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var replay = await Send(); Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
        authority.Setup(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Write, "/customers/42/replacementcases", It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Denied));
        using var denied = await Send(); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        repository.Verify(x => x.CreateAsync(42, ReplacementReason.CarrierDamage, It.IsAny<IReadOnlyList<ReplacementAffectedInput>>(), It.IsAny<ReplacementEvidence>(), 7, It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Fact]
    public async Task Rejected_case_revision_is_explicitly_distinct_from_unknown_operation_conflict()
    {
        enabled = true; Staff(ReplacementPermissions.Write); Seed();
        authority.Setup(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Write, "/replacementcases/3", It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        repository.Setup(x => x.ExecuteAsync(3, 1, It.IsAny<Guid>(), 7, It.IsAny<ReplacementCommand>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ThrowsAsync(new ReplacementConflictException("Case revision changed.", true));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/replacementcases/3/commands") { Content = JsonContent.Create(new { ExpectedRevision = 1, Command = new { Kind = "StartAttempt", OrderId = 10, Quantity = 1 } }, options: new JsonSerializerOptions(JsonSerializerDefaults.General)) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D")); using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal("Revision", Assert.Single(response.Headers.GetValues("X-Replacement-Rejection")));
    }
    [Fact]
    public async Task PostgreSql_http_lost_ack_reconciles_exact_actor_receipt_without_second_case_or_original_mutation()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderDbContext(options); await db.Database.MigrateAsync();
        var original = new Order
        {
            CustomerId = 42,
            Quantity = 1,
            Manufactured = 1,
            TrackingNumber = "LALAMOVE",
            FinishedDate = new DateTime(2026, 9, 30),
            UnitPrice = 100,
            Process = new Process { Name = "Mold", Category = new Category { Name = "Manufacturing" } }
        };
        db.Add(original); await db.SaveChangesAsync();
        var originalModified = original.ModifiedDate;
        var store = new ReplacementStore(() => new OrderDbContext(options)); durableRepository = store; enabled = true; failCacheCompletion = true;
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        var operation = Guid.NewGuid(); var reference = new ReplacementEvidence(Guid.NewGuid(), Guid.NewGuid());
        var input = new ReportReplacementRequest(42, ReplacementReason.ManufacturingNonconformance, [new(original.Id, 1)], reference);
        async Task<HttpResponseMessage> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/replacementcases/customers/42") { Content = JsonContent.Create(input, options: ReportWire()) };
            request.Headers.Add("Idempotency-Key", operation.ToString("D")); return await client.SendAsync(request);
        }
        Staff(ReplacementPermissions.Write);
        using var lost = await Send(); Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        using var pending = await Send(); Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        var receipt = await store.GetOperationAsync(7, operation, default); Assert.NotNull(receipt); Assert.Equal(1, receipt.Value.Revision);
        await store.ExecuteAsync(receipt.Id, 1, Guid.NewGuid(), 7, new ApproveReplacement(ReturnDecision.Waived, false, false, "No return required"), DateTimeOffset.UtcNow, default);
        client.DefaultRequestHeaders.Remove("X-Test-Permission"); client.DefaultRequestHeaders.Add("X-Test-Permission", ReplacementPermissions.Read);
        using var reconcile = await client.GetAsync($"/replacementcases/customers/42/operations/{operation:D}"); Assert.Equal(HttpStatusCode.OK, reconcile.StatusCode);
        using var body = JsonDocument.Parse(await reconcile.Content.ReadAsStringAsync());
        Assert.Equal(receipt.Id, body.RootElement.GetProperty("Id").GetInt32()); Assert.Equal(1, body.RootElement.GetProperty("Value").GetProperty("Revision").GetInt32());
        Assert.Equal(2, (await store.GetAsync(receipt.Id, default))!.Value.Revision);
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 8, "trusted"));
        using var otherActor = await client.GetAsync($"/replacementcases/customers/42/operations/{operation:D}"); Assert.Equal(HttpStatusCode.NotFound, otherActor.StatusCode);
        db.ChangeTracker.Clear(); var unchanged = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == original.Id);
        Assert.Equal(1, unchanged.Manufactured); Assert.Equal("LALAMOVE", unchanged.TrackingNumber); Assert.Equal(new DateTime(2026, 9, 30), unchanged.FinishedDate); Assert.Equal(originalModified, unchanged.ModifiedDate);
        Assert.Single(await db.Set<ReplacementCaseRow>().ToListAsync()); Assert.Equal(2, await db.Set<ReplacementOperationRow>().CountAsync());
    }
    private sealed class FaultingCompletion(IIdempotencyStore inner, Func<bool> fail) : IIdempotencyStore
    {
        public Task<IdempotencyAcquireResult<T>> AcquireAsync<T>(string scope, string key, string fingerprint, CancellationToken c) where T : class => inner.AcquireAsync<T>(scope, key, fingerprint, c);
        public Task CompleteAsync<T>(string scope, string key, string fingerprint, string reservation, T response, CancellationToken c) where T : class
            => fail() ? Task.FromException(new IdempotencyStoreUnavailableException("Simulated cache acknowledgment failure after durable commit.")) : inner.CompleteAsync(scope, key, fingerprint, reservation, response, c);
        public Task ReleaseAsync(string scope, string key, string reservation, CancellationToken c) => inner.ReleaseAsync(scope, key, reservation, c);
    }
    private static JsonSerializerOptions ReportWire()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General); options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(allowIntegerValues: false)); return options;
    }
    private ReplacementStoredCase Seed()
    {
        var value = new ReplacementStoredCase(3, ReplacementCase.Create(42, ReplacementReason.CarrierDamage, [new(10, 42, 1, 1, 1, "Original", null, null, null)], new(Guid.NewGuid(), Guid.NewGuid()), 7, DateTimeOffset.UtcNow));
        repository.Setup(x => x.GetAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(value);
        repository.Setup(x => x.OriginalsMatchAsync(value.Value, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        return value;
    }
    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Subject")) return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "staff") };
            if (Request.Headers.TryGetValue("X-Test-Permission", out var p)) claims.Add(new("permission", p.ToString()));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")), "Test")));
        }
    }
}
