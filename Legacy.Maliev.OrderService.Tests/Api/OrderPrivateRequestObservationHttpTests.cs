using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.OrderService.Tests.Api;

public sealed class OrderPrivateRequestObservationHttpTests
{
    private const string InstanceHeader = "X-Maliev-Health-Instance";
    private const string NonceHeader = "X-Maliev-Diagnostic-Id";
    private const string DiagnosticPath = "/internal/diagnostics/observability";

    [Fact]
    public async Task ProductionProgram_RegisteredGetHealthHasStablePerHostIdentityWithoutProbeEvents()
    {
        await using var first = new ObservationHost();
        using var firstClient = first.OpenClient();
        Assert.True(first.Services.GetRequiredService<IHostEnvironment>().IsProduction());
        Assert.IsNotType<TestServer>(first.Services.GetRequiredService<IServer>());
        string? identity = null;
        for (var repeat = 0; repeat < 2; repeat++)
        {
            foreach (var path in new[] { "/order/liveness", "/order/readiness" })
            {
                using var response = await firstClient.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var marker = HealthMarker(response);
                Assert.False(response.Headers.Contains(NonceHeader));
                identity ??= marker;
                Assert.Equal(identity, marker);
            }
        }
        Assert.Empty(first.Records.Snapshot);

        await using var second = new ObservationHost();
        using var secondClient = second.OpenClient();
        using var other = await secondClient.GetAsync("/order/liveness");
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        var otherIdentity = HealthMarker(other);
        Assert.NotEqual(identity, otherIdentity);
        Assert.False(first.Records.Overflowed);
        Assert.False(second.Records.Overflowed);
    }

    [Theory]
    [InlineData("POST", "/order/liveness")]
    [InlineData("HEAD", "/order/liveness")]
    [InlineData("POST", "/order/readiness")]
    [InlineData("GET", "/order/liveness/extra")]
    [InlineData("GET", "/order/aspire-liveness")]
    [InlineData("GET", "/orders/liveness")]
    [InlineData("GET", "/orderstatuses/readiness")]
    [InlineData("GET", "/liveness")]
    public async Task ProductionProgram_NonGetAndUnregisteredPathsDoNotAcquireHealthIdentity(string method, string path)
    {
        await using var host = new ObservationHost();
        using var client = host.OpenClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await client.SendAsync(request);
        Assert.False(response.Headers.Contains(InstanceHeader));
        Assert.False(response.Headers.Contains(NonceHeader));
        Assert.Empty(host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);
    }

    [Fact]
    public async Task ProductionProgram_DeniedRequestsAreQuietAndDoNotConsumeTheProbeBudget()
    {
        await using var host = new ObservationHost();
        using var client = host.OpenClient();
        foreach (var nonce in new string?[] { null, "not-a-guid", Guid.NewGuid().ToString("D") })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            if (nonce is not null) request.Headers.Add(NonceHeader, nonce);
            using var response = await client.SendAsync(request);
            AssertDenied(response);
        }
        using var repeated = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
        repeated.Headers.Add(NonceHeader, new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") });
        using var repeatedResponse = await client.SendAsync(repeated);
        AssertDenied(repeatedResponse);
        using var post = new HttpRequestMessage(HttpMethod.Post, DiagnosticPath);
        post.Headers.Add(NonceHeader, Guid.NewGuid().ToString("N"));
        using var postResponse = await client.SendAsync(post);
        AssertDenied(postResponse);
        Assert.Empty(host.Records.Snapshot);
        using var admitted = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
        var admittedNonce = Guid.NewGuid().ToString("N");
        admitted.Headers.Add(NonceHeader, admittedNonce);
        using var accepted = await client.SendAsync(admitted);
        Assert.Equal(HttpStatusCode.InternalServerError, accepted.StatusCode);
        Assert.Equal(admittedNonce, Assert.Single(accepted.Headers.GetValues(NonceHeader)));
        Assert.Single(host.Records.Snapshot, value => value == ObservationEvent.Probe);
        Assert.Single(host.Records.Snapshot, value => value == ObservationEvent.FrameworkError);
        Assert.Single(host.Records.Snapshot, value => value == ObservationEvent.Unhandled);
        Assert.False(host.Records.Overflowed);
    }

    [Fact]
    public async Task ProductionProgram_AdmittedLoopbackProbeCorrelatesIdentityAndThrottlesUntilClockAdvances()
    {
        await using var host = new ObservationHost();
        using var client = host.OpenClient();
        using var health = await client.GetAsync("/order/liveness");
        var identity = HealthMarker(health);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var nonce = attempt == 0 ? "abcdefabcdefabcdefabcdefabcdefab" : "00000000000000000000000000000000";
            using var request = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            request.Headers.Add(NonceHeader, nonce.ToUpperInvariant());
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(nonce, Assert.Single(response.Headers.GetValues(NonceHeader)));
            Assert.Equal(identity, Assert.Single(response.Headers.GetValues(InstanceHeader)));
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
            using var throttled = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            throttled.Headers.Add(NonceHeader, Guid.NewGuid().ToString("N"));
            using var rejected = await client.SendAsync(throttled);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.False(rejected.Headers.Contains(InstanceHeader));
            Assert.False(rejected.Headers.Contains(NonceHeader));
            host.Clock.Advance(TimeSpan.FromMilliseconds(59999));
            using var early = new HttpRequestMessage(HttpMethod.Get, DiagnosticPath);
            early.Headers.Add(NonceHeader, Guid.NewGuid().ToString("N"));
            using var earlyResponse = await client.SendAsync(early);
            Assert.Equal(HttpStatusCode.TooManyRequests, earlyResponse.StatusCode);
            Assert.False(earlyResponse.Headers.Contains(InstanceHeader));
            Assert.False(earlyResponse.Headers.Contains(NonceHeader));
            host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.Equal(2, host.Records.Snapshot.Count(value => value == ObservationEvent.Probe));
        Assert.Equal(2, host.Records.Snapshot.Count(value => value == ObservationEvent.FrameworkError));
        Assert.Equal(2, host.Records.Snapshot.Count(value => value == ObservationEvent.Unhandled));
        Assert.DoesNotContain(ObservationEvent.HealthFailure, host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);
    }

    [Fact]
    public async Task ProductionProgram_DirectNonLoopbackPeerCannotSpoofLoopbackThroughTrustedForwardedHeader()
    {
        // TestServer only supplies the transport peer for this guard case; all positive identity proof uses Kestrel.
        await using var host = new ObservationHost(kestrel: false);
        using var client = host.OpenClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = await host.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
            context.Request.Method = "GET";
            context.Request.Path = DiagnosticPath;
            context.Request.Headers[NonceHeader] = Guid.NewGuid().ToString("N");
            context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        }, deadline.Token);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey(InstanceHeader));
        Assert.False(context.Response.Headers.ContainsKey(NonceHeader));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Empty(host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);
    }

    [Fact]
    public async Task ProductionProgram_OrdinaryReturnedAndThrownFailuresKeepTheirExistingResponseBoundary()
    {
        await using var host = new ObservationHost();
        using var client = host.OpenClient();
        foreach (var path in new[] { "/Orders", "/OrderStatuses" })
        {
            using var denied = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.False(denied.Headers.Contains(InstanceHeader));
            Assert.False(denied.Headers.Contains(NonceHeader));
        }
        using var returned = await client.GetAsync("/__fixture/observation/returned");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, returned.StatusCode);
        using var returnedJson = JsonDocument.Parse(await returned.Content.ReadAsStringAsync());
        Assert.Equal("fixture-returned-failure", returnedJson.RootElement.GetProperty("error").GetString());
        Assert.False(returned.Headers.Contains(InstanceHeader));
        Assert.False(returned.Headers.Contains(NonceHeader));
        using var thrown = await client.GetAsync("/__fixture/observation/throw");
        Assert.Equal(HttpStatusCode.InternalServerError, thrown.StatusCode);
        Assert.Equal("application/json", thrown.Content.Headers.ContentType?.MediaType);
        var body = await thrown.Content.ReadAsStringAsync();
        using var thrownJson = JsonDocument.Parse(body);
        Assert.Equal("An internal server error occurred", thrownJson.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, thrownJson.RootElement.GetProperty("details").ValueKind);
        Assert.DoesNotContain("fixture-private-error-sentinel", body, StringComparison.Ordinal);
        Assert.False(thrown.Headers.Contains(InstanceHeader));
        Assert.False(thrown.Headers.Contains(NonceHeader));
        Assert.Single(host.Records.Snapshot, value => value == ObservationEvent.Handled);
        Assert.Single(host.Records.Snapshot, value => value == ObservationEvent.Unhandled);
        Assert.DoesNotContain(ObservationEvent.Probe, host.Records.Snapshot);
        Assert.DoesNotContain(ObservationEvent.FrameworkError, host.Records.Snapshot);
        Assert.False(host.Records.Overflowed);
    }

    private static string HealthMarker(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.True(response.Headers.TryGetValues(InstanceHeader, out var values));
        Assert.NotNull(values);
        var marker = Assert.Single(values);
        Assert.True(Guid.TryParseExact(marker, "N", out _));
        return marker;
    }

    private static void AssertDenied(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains(InstanceHeader));
        Assert.False(response.Headers.Contains(NonceHeader));
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("noindex", Assert.Single(response.Headers.GetValues("X-Robots-Tag")));
    }

    private sealed class ObservationHost : WebApplicationFactory<Program>
    {
        private readonly RSA rsa;
        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        public EnumRecorder Records { get; } = new();

        public ObservationHost(bool kestrel = true)
        {
            if (string.Equals(Environment.GetEnvironmentVariable("MALIEV_OBSERVABILITY_READ_ONLY_STARTUP"), "true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Observation fixture refuses ambient database-validation startup mode.");
            rsa = RSA.Create(2048);
            try
            {
                if (kestrel) UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            }
            catch
            {
                rsa.Dispose();
                throw;
            }
        }

        public HttpClient OpenClient()
        {
            StartServer();
            Assert.False(Services.GetRequiredService<IOptions<OrderDeletionWorkerOptions>>().Value.Enabled);
            Assert.False(Services.GetRequiredService<IOptions<OrderDeletionRecoveryOptions>>().Value.Enabled);
            var server = Services.GetRequiredService<IServer>();
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false };
            if (server is not TestServer)
            {
                var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses
                    ?? throw new InvalidOperationException("Kestrel did not publish a bound address.");
                var address = new Uri(Assert.Single(addresses), UriKind.Absolute);
                Assert.Equal(Uri.UriSchemeHttp, address.Scheme);
                Assert.True(IPAddress.TryParse(address.Host, out var peer)
                    && peer is not null && IPAddress.IsLoopback(peer));
                Assert.InRange(address.Port, 1, 65535);
                Assert.Equal("/", address.AbsolutePath);
                Assert.Empty(address.UserInfo);
                Assert.Empty(address.Query);
                Assert.Empty(address.Fragment);
                options.BaseAddress = address;
            }
            var client = CreateClient(options);
            client.Timeout = TimeSpan.FromSeconds(15);
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in new Dictionary<string, string>
            {
                ["ConnectionStrings:OrderDbContext"] = "Host=127.0.0.1;Port=1;Database=unused;Username=fixture;Pooling=false",
                ["ConnectionStrings:OrderStatusDbContext"] = "Host=127.0.0.1;Port=1;Database=unused;Username=fixture;Pooling=false",
                ["OrderDeletionWorker:Enabled"] = "false",
                ["OrderDeletionRecovery:Enabled"] = "false",
                ["Cache:RedisEnabled"] = "false",
                ["CORS:AllowedOrigins:0"] = "https://example.test",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = "order-observation-fixture",
                ["Jwt:Audience"] = "order-observation-fixture",
                ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.7",
                ["Observability:TracingEnabled"] = "false",
                ["Observability:RuntimeMetricsEnabled"] = "false",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            }) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.AddProvider(Records));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
                // These existing production workers must stay inert in this observation-only fixture.
                services.PostConfigure<OrderDeletionWorkerOptions>(options => options.Enabled = false);
                services.PostConfigure<OrderDeletionRecoveryOptions>(options => options.Enabled = false);
                // Isolate external health checks only. This proves the registered response marker, NOT actual dependency readiness.
                services.PostConfigure<HealthCheckServiceOptions>(options => options.Registrations.Clear());
                services.Configure<HostOptions>(options =>
                {
                    options.StartupTimeout = TimeSpan.FromSeconds(20);
                    options.ShutdownTimeout = TimeSpan.FromSeconds(10);
                });
                services.AddControllers().AddApplicationPart(typeof(OrderObservationFailureController).Assembly);
            });
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { rsa.Dispose(); }
        }
    }

    private enum ObservationEvent { Probe, FrameworkError, Unhandled, Handled, HealthFailure }

    private sealed class EnumRecorder : ILoggerProvider
    {
        private readonly Queue<ObservationEvent> values = new();
        private readonly object gate = new();
        private bool overflowed;
        public ObservationEvent[] Snapshot { get { lock (gate) return values.ToArray(); } }
        public bool Overflowed { get { lock (gate) return overflowed; } }
        public ILogger CreateLogger(string categoryName) => new EnumLogger(this,
            categoryName == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware");
        public void Dispose() { }
        private void Record<TState>(LogLevel level, EventId eventId, bool frameworkCategory, TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var name = fields.FirstOrDefault(field => field.Key == "EventName").Value as string;
            ObservationEvent? value = frameworkCategory && level == LogLevel.Error
                && eventId.Id == 1 && eventId.Name == "UnhandledException"
                ? ObservationEvent.FrameworkError : name switch
                {
                    "ObservabilityPipelineProbe" when level == LogLevel.Warning => ObservationEvent.Probe,
                    "UnhandledRequestFailure" when level == LogLevel.Critical => ObservationEvent.Unhandled,
                    "HandledOperationFailure" when level == LogLevel.Error => ObservationEvent.Handled,
                    "HealthProbeFailure" when level == LogLevel.Error => ObservationEvent.HealthFailure,
                    _ => null
                };
            if (value is null) return;
            lock (gate)
            {
                if (values.Count >= 16) { overflowed = true; return; }
                values.Enqueue(value.Value);
            }
        }
        private sealed class EnumLogger(EnumRecorder owner, bool frameworkCategory) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Record(logLevel, eventId, frameworkCategory, state);
        }
    }
}

// Only discovered by the test-only application part; never registered by the production project.
[ApiController]
[AllowAnonymous]
[Route("/__fixture/observation")]
public sealed class OrderObservationFailureController : ControllerBase
{
    [HttpGet("returned")]
    public IActionResult Returned() => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "fixture-returned-failure" });
    [HttpGet("throw")]
    public IActionResult Throw() => throw new Exception("fixture-private-error-sentinel");
}
