using System.Net;
using System.Text.Json;
using Legacy.Maliev.OrderService.Domain.Replacement;

namespace Legacy.Maliev.OrderService.Data.Replacement;

public sealed class ReplacementEvidenceUnavailableException : Exception;
/// <summary>Frozen FileService receipt read DTO; does not own document storage, verification or access policy.</summary>
public sealed record ReplacementEvidenceReceipt(Guid DocumentId, Guid VersionId, int CustomerId, string Kind,
    string ContentSha256, int? QuotationId, IReadOnlyList<int> OrderIds, string VerificationStatus,
    string? VerifiedBySubject, DateTimeOffset? VerifiedAtUtc, long Revision);

/// <summary>Consumes current exact-version receipt authority. Not registered until the shared producer is accepted.
/// The configured authenticated client must carry current staff/tenant authority and scoped permissions.</summary>
public sealed class ReplacementEvidenceClient(HttpClient client)
{
    public async Task<ReplacementEvidenceReceipt> VerifyAsync(int customerId, IReadOnlyList<int> originalOrderIds,
        ReplacementEvidence reference, string requiredKind, CancellationToken token)
    {
        var ids = originalOrderIds.ToArray();
        if (customerId <= 0 || ids.Length is < 1 or > 100 || ids.Any(x => x <= 0) || ids.Distinct().Count() != ids.Length ||
            reference.DocumentId == Guid.Empty || reference.VersionId == Guid.Empty ||
            requiredKind is not ("Evidence" or "Shipment" or "Acceptance" or "Release"))
            throw new ReplacementEvidenceUnavailableException();
        try
        {
            using var response = await client.GetAsync(
                $"customers/{customerId}/documents/{reference.DocumentId:D}/versions/{reference.VersionId:D}/receipt",
                HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 65536)
                throw new ReplacementEvidenceUnavailableException();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, token)) != 0)
            {
                if (buffer.Length + read > 65536) throw new ReplacementEvidenceUnavailableException();
                buffer.Write(chunk, 0, read);
            }
            var receipt = JsonSerializer.Deserialize<ReplacementEvidenceReceipt>(buffer.ToArray());
            if (receipt is null || receipt.DocumentId != reference.DocumentId || receipt.VersionId != reference.VersionId ||
                receipt.CustomerId != customerId || receipt.Kind != requiredKind || receipt.VerificationStatus != "Verified" ||
                receipt.Revision <= 0 || string.IsNullOrWhiteSpace(receipt.VerifiedBySubject) ||
                receipt.VerifiedAtUtc is null || receipt.VerifiedAtUtc.Value.Offset != TimeSpan.Zero ||
                receipt.ContentSha256 is null || receipt.ContentSha256.Length != 64 ||
                receipt.ContentSha256.Any(x => x is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
                receipt.OrderIds is null || receipt.OrderIds.Count != ids.Length ||
                receipt.OrderIds.Distinct().Count() != receipt.OrderIds.Count || receipt.OrderIds.Any(x => !ids.Contains(x)) ||
                receipt.QuotationId is <= 0)
                throw new ReplacementEvidenceUnavailableException();
            return receipt with { OrderIds = Array.AsReadOnly(receipt.OrderIds.ToArray()) };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException ||
            exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new ReplacementEvidenceUnavailableException();
        }
    }
}
