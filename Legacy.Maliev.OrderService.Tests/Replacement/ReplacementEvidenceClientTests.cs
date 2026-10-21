using System.Net;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Data.Replacement;
using Legacy.Maliev.OrderService.Domain.Replacement;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

public sealed class ReplacementEvidenceClientTests
{
    private static readonly Guid DocumentId = Guid.NewGuid(), VersionId = Guid.NewGuid();
    private static readonly ReplacementEvidence Reference = new(DocumentId, VersionId);
    private static Dictionary<string, object?> Receipt() => new()
    {
        ["DocumentId"] = DocumentId,
        ["VersionId"] = VersionId,
        ["CustomerId"] = 42,
        ["Kind"] = "Evidence",
        ["ContentSha256"] = new string('a', 64),
        ["QuotationId"] = null,
        ["OrderIds"] = new[] { 94826 },
        ["VerificationStatus"] = "Verified",
        ["VerifiedBySubject"] = "employee-7",
        ["VerifiedAtUtc"] = "2026-10-08T06:14:22Z",
        ["Revision"] = 1
    };
    private static ReplacementEvidenceClient Reader(Dictionary<string, object?> receipt, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new Handler(JsonSerializer.Serialize(receipt), status)) { BaseAddress = new Uri("https://files.invalid/") });

    [Fact]
    public async Task Exact_frozen_wire_returns_immutable_order_scoped_receipt()
    {
        var receipt = await Reader(Receipt()).VerifyAsync(42, [94826], Reference, "Evidence", default);
        Assert.Equal(DocumentId, receipt.DocumentId); Assert.Equal(VersionId, receipt.VersionId);
        Assert.Equal(new string('a', 64), receipt.ContentSha256);
        Assert.Equal([94826], receipt.OrderIds);
    }

    [Theory]
    [InlineData("CustomerId", 99)]
    [InlineData("Kind", "Corporate")]
    [InlineData("VerificationStatus", "PendingVerification")]
    [InlineData("VerifiedBySubject", "")]
    [InlineData("Revision", 0)]
    [InlineData("ContentSha256", "not-a-digest")]
    [InlineData("VerifiedAtUtc", "2026-10-08T06:14:22+07:00")]
    public async Task Unverified_noncanonical_or_unrelated_receipt_denies(string field, object bad)
    {
        var receipt = Receipt(); receipt[field] = bad;
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => Reader(receipt).VerifyAsync(42, [94826], Reference, "Evidence", default));
    }

    [Fact]
    public async Task Wrong_version_or_missing_exact_affected_order_denies()
    {
        var receipt = Receipt(); receipt["VersionId"] = Guid.NewGuid();
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => Reader(receipt).VerifyAsync(42, [94826], Reference, "Evidence", default));
        receipt = Receipt(); receipt["OrderIds"] = new[] { 94876 };
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => Reader(receipt).VerifyAsync(42, [94826], Reference, "Evidence", default));
        receipt = Receipt(); receipt["OrderIds"] = Array.Empty<int>();
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => Reader(receipt).VerifyAsync(42, [94826], Reference, "Evidence", default));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(503)]
    public async Task Denied_missing_unavailable_producer_never_supplies_authority(int status) =>
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => Reader(Receipt(), (HttpStatusCode)status).VerifyAsync(42, [94826], Reference, "Evidence", default));

    [Fact]
    public async Task Malformed_and_oversized_stream_are_bounded_failures()
    {
        foreach (var body in new[] { "badjson", new string('x', 65537) })
        {
            var reader = new ReplacementEvidenceClient(new HttpClient(new Handler(body, HttpStatusCode.OK)) { BaseAddress = new Uri("https://files.invalid/") });
            await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => reader.VerifyAsync(42, [94826], Reference, "Evidence", default));
        }
    }

    [Fact]
    public async Task Receipt_route_uses_exact_customer_document_and_version_only()
    {
        var handler = new Handler(JsonSerializer.Serialize(Receipt()), HttpStatusCode.OK);
        var reader = new ReplacementEvidenceClient(new HttpClient(handler) { BaseAddress = new Uri("https://files.invalid/") });
        await reader.VerifyAsync(42, [94826], Reference, "Evidence", default);
        Assert.Equal($"/customers/42/documents/{DocumentId:D}/versions/{VersionId:D}/receipt", handler.Request!.AbsolutePath);
        Assert.Empty(handler.Request.Query);
    }
    [Fact]
    public async Task Interrupted_receipt_stream_is_dependency_unavailable()
    {
        var reader = new ReplacementEvidenceClient(new HttpClient(new InterruptedHandler()) { BaseAddress = new Uri("https://files.invalid/") });
        await Assert.ThrowsAsync<ReplacementEvidenceUnavailableException>(() => reader.VerifyAsync(42, [94826], Reference, "Evidence", default));
    }

    private sealed class InterruptedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) });
    }
    private sealed class InterruptedStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(new IOException("Owned interrupted receipt response"));
    }

    private sealed class Handler(string body, HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
