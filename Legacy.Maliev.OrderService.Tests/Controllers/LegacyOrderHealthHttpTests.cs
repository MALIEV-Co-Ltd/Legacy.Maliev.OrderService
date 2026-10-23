using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

/// <summary>Checks legacy health URLs through the actual Order Program with controlled health results.</summary>
public sealed class LegacyOrderHealthHttpTests
{
    /// <summary>Legacy liveness retains anonymous, case-insensitive and unconstrained-method admission.</summary>
    /// <returns>A task completing the real HTTP assertions.</returns>
    [Fact]
    public async Task LegacyLiveness_PreservesAnonymousVerbsAndCaseWithoutDependencyCalls()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch, HttpMethod.Options })
        {
            foreach (var path in new[] { "/orders/liveness", "/OrDeRs/LiVeNeSs" })
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(method, path);
                using var response = await client.SendAsync(request, deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(deadline.Token));
                Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.True(response.Headers.CacheControl?.NoCache);
                Assert.False(response.Headers.Contains("Location"));
                Assert.False(response.Headers.Contains("WWW-Authenticate"));
            }
        }
        Assert.Equal(0, factory.Probe.Calls);
    }

    /// <summary>Readiness hides check details and keeps the shared cross-prefix failure throttle and success reset.</summary>
    /// <returns>A task completing the real HTTP and bounded logging assertions.</returns>
    [Fact]
    public async Task Readiness_PreservesPrivateFailureDeduplicationAndSuccessReset()
    {
        await using var factory = new HealthFactory(HealthStatus.Unhealthy);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var path in new[] { "/orders/readiness", "/order/readiness" })
        {
            using var response = await client.GetAsync(path, deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("PRIVATE-HEALTH-DETAIL", body, StringComparison.Ordinal);
            if (path == "/orders/readiness") Assert.Equal("Unhealthy", body);
        }
        Assert.Equal(4, factory.Probe.Calls);
        AssertFailure(Assert.Single(factory.Observation.Snapshot()));

        factory.Probe.Status = HealthStatus.Healthy;
        using (var success = await client.GetAsync("/orders/readiness", deadline.Token))
            Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        factory.Probe.Status = HealthStatus.Unhealthy;
        using (var repeated = await client.GetAsync("/order/readiness", deadline.Token))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, repeated.StatusCode);
        Assert.Equal(8, factory.Probe.Calls);
        var entries = factory.Observation.Snapshot();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, AssertFailure);
        Assert.False(factory.Observation.Overflowed);
    }

    /// <summary>An untagged degraded dependency remains part of current stronger readiness.</summary>
    /// <returns>A task completing the real HTTP assertions.</returns>
    [Fact]
    public async Task LegacyReadiness_IncludesUntaggedChecksUnderCurrentStrongerPolicy()
    {
        await using var factory = new HealthFactory(HealthStatus.Degraded);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/OrDeRs/ReAdInEsS");
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync(deadline.Token));
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.NoCache);
        Assert.Contains(response.Headers.Pragma, header => header.Name == "no-cache");
        Assert.Equal(DateTimeOffset.UnixEpoch, response.Content.Headers.Expires);
        Assert.Equal(2, factory.Probe.Calls);
    }

    /// <summary>Only registered current GET health endpoints expose instance identification.</summary>
    /// <returns>A task completing the real HTTP header assertions.</returns>
    [Fact]
    public async Task LegacyAliases_DoNotAcquireCurrentHealthInstanceHeaders()
    {
        await using var factory = new HealthFactory(HealthStatus.Healthy);
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/orders/liveness", "/orders/readiness", "/order/liveness", "/order/readiness" })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(response.Headers.Contains("X-Maliev-Diagnostic-Id"));
            if (path.StartsWith("/orders/", StringComparison.Ordinal))
                Assert.False(response.Headers.Contains("X-Maliev-Health-Instance"));
            else
                Assert.True(Guid.TryParseExact(Assert.Single(response.Headers.GetValues("X-Maliev-Health-Instance")), "N", out _));
        }
        Assert.Equal(4, factory.Probe.Calls);
    }

    /// <summary>Health aliases do not authorize business reads or create catch-all health routes.</summary>
    /// <returns>A task completing the real HTTP denial assertions.</returns>
    [Fact]
    public async Task HealthAliases_PreserveBusinessAuthenticationAndNearRouteMisses()
    {
        await using var factory = new HealthFactory(HealthStatus.Healthy);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var business = await client.GetAsync("/orders/1", deadline.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, business.StatusCode);
        foreach (var path in new[] { "/orders/liveness-more", "/orders/readiness/child", "/orders/not-a-health-route" })
        {
            using var response = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        Assert.Equal(0, factory.Probe.Calls);
    }

    private static void AssertFailure(ObservationCapture.Entry entry)
    {
        Assert.Equal("HealthProbeFailure", entry.EventName);
        Assert.Equal("Readiness", entry.Operation);
        Assert.Equal(503, entry.StatusCode);
        Assert.False(entry.ExceptionPresent);
        Assert.Equal(new[] { "EventName", "Operation", "StatusCode", "{OriginalFormat}" }, entry.Keys);
    }

    private sealed class HealthFactory(HealthStatus status) : WebApplicationFactory<Program>
    {
        private readonly RSA signingKey = RSA.Create(2048);
        internal ControlledProbe Probe { get; } = new(status);
        internal ObservationCapture Observation { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureLogging(logging => logging.AddProvider(Observation));
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderDbContext"] = "Host=127.0.0.1;Port=1;Database=unused_order;Username=unused;Pooling=false",
                ["ConnectionStrings:OrderStatusDbContext"] = "Host=127.0.0.1;Port=1;Database=unused_status;Username=unused;Pooling=false",
                ["Cache:RedisEnabled"] = "false",
                ["OrderDeletionWorker:Enabled"] = "false",
                ["OrderDeletionRecovery:Enabled"] = "false",
                ["CORS:AllowedOrigins:0"] = "https://order-health-fixture.invalid",
                ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.40",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(signingKey.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "https://order-health-fixture.invalid",
                ["Jwt:Audience"] = "order-health-fixture",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false",
            };
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            builder.ConfigureServices(services =>
            {
                services.Configure<HostOptions>(options => options.StartupTimeout = TimeSpan.FromSeconds(30));
                services.PostConfigure<HealthCheckServiceOptions>(options =>
                {
                    // Keep actual Program routing and middleware; control only dependency health outcomes.
                    options.Registrations.Clear();
                    options.Registrations.Add(new HealthCheckRegistration("tagged", _ => Probe, null, ["ready"]));
                    options.Registrations.Add(new HealthCheckRegistration("untagged", _ => Probe, null, []));
                });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { signingKey.Dispose(); }
        }
    }

    // Capture only bounded observation metadata, never formatter output or request/health details.
    private sealed class ObservationCapture : ILoggerProvider
    {
        private readonly object gate = new();
        private readonly List<Entry> entries = [];
        internal bool Overflowed { get; private set; }
        internal sealed record Entry(string? EventName, string? Operation, int? StatusCode, bool ExceptionPresent, string[] Keys);
        internal Entry[] Snapshot()
        {
            lock (gate) return entries.ToArray();
        }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this,
            categoryName == "Maliev.Aspire.ServiceDefaults.Diagnostics.PrivateRequestObservation");
        public void Dispose() { }

        private sealed class CaptureLogger(ObservationCapture owner, bool observation) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => observation && level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(level)) return;
                var properties = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToArray() : [];
                var entry = new Entry(
                    properties.SingleOrDefault(pair => pair.Key == "EventName").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "Operation").Value as string,
                    properties.SingleOrDefault(pair => pair.Key == "StatusCode").Value is int code ? code : null,
                    exception is not null,
                    properties.Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray());
                lock (owner.gate)
                {
                    if (owner.entries.Count == 16) owner.Overflowed = true;
                    else owner.entries.Add(entry);
                }
            }
        }
    }

    private sealed class ControlledProbe(HealthStatus status) : IHealthCheck
    {
        private int calls;
        private int currentStatus = (int)status;
        internal int Calls => Volatile.Read(ref calls);
        internal HealthStatus Status
        {
            get => (HealthStatus)Volatile.Read(ref currentStatus);
            set => Volatile.Write(ref currentStatus, (int)value);
        }
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HealthCheckResult(
                context.Registration.Tags.Contains("ready") ? HealthStatus.Healthy : Status, "PRIVATE-HEALTH-DETAIL"));
        }
    }
}
