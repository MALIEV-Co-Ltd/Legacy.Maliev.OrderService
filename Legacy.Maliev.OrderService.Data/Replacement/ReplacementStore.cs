using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data.Replacement;

public sealed class ReplacementStoreConflictException(string message) : Exception(message);
public sealed class ReplacementStoreNotFoundException : Exception;
/// <summary>Atomic case command storage in the Order database. The application must provide current authority and
/// verify protected evidence before calling; this component never writes an Order, invoice or status.</summary>
public sealed class ReplacementStore(Func<DbContext> contexts)
{
    public async Task<ReplacementStoredCase> CreateAsync(int customerId, ReplacementReason reason,
        IReadOnlyList<ReplacementAffectedInput> affected, ReplacementEvidence evidence, int employeeId,
        Guid operationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ValidateOperation(employeeId, operationId);
        var hash = Hash(new { customerId, reason, affected, evidence });
        return await TransactionAsync(async db =>
        {
            await LockOperationAsync(db, employeeId, operationId, cancellationToken);
            var replay = await ReplayAsync(db, employeeId, operationId, hash, cancellationToken);
            if (replay is not null) return replay;
            if (affected.Count is < 1 or > 100 || affected.Select(x => x.OrderId).Distinct().Count() != affected.Count)
                throw new ReplacementRuleException("Select distinct affected orders.");
            var ids = affected.Select(x => x.OrderId).ToArray();
            // Share the canonical legacy Order lifetime lock, in sorted order, before reading mutable authority.
            foreach (var id in ids.Order())
            {
                var lifetime = $"legacy-order-lifetime\n{id.ToString(CultureInfo.InvariantCulture)}";
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lifetime}, 0))", cancellationToken);
            }
            if (await db.Set<OrderDeletionIntent>().AsNoTracking().AnyAsync(x => ids.Contains(x.OrderId), cancellationToken))
                throw new ReplacementRuleException("Original order lifetime is deleted.");
            var orders = await db.Set<Order>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken);
            if (orders.Count != ids.Length || orders.Any(x => x.CustomerId != customerId))
                throw new ReplacementRuleException("Original orders are missing or belong to another customer.");
            var originals = affected.Select(input =>
            {
                var order = orders.Single(x => x.Id == input.OrderId);
                return new ReplacementOriginal(order.Id, customerId, order.Quantity, order.Manufactured, input.Quantity,
                    order.TrackingNumber, order.FinishedDate is { } date ? DateOnly.FromDateTime(date) : null, null, null);
            }).ToArray();
            var value = ReplacementCase.Create(customerId, reason, originals, evidence, employeeId, now);
            var row = new ReplacementCaseRow
            {
                CustomerId = customerId,
                Reason = reason,
                OriginalsJson = JsonSerializer.Serialize(originals),
                EvidenceJson = JsonSerializer.Serialize(evidence),
                ReportedBy = employeeId,
                ReportedAt = now,
                Revision = value.Revision
            };
            db.Add(row); await db.SaveChangesAsync(cancellationToken);
            db.AddRange(originals.Select(x => new ReplacementAffectedRow { CaseId = row.Id, OrderId = x.OrderId }));
            db.Add(Receipt(employeeId, operationId, hash, row.Id, value.Revision));
            await db.SaveChangesAsync(cancellationToken);
            return new ReplacementStoredCase(row.Id, value);
        }, cancellationToken);
    }

    public async Task<ReplacementStoredCase> ExecuteAsync(int caseId, int expectedRevision, Guid operationId,
        int employeeId, ReplacementCommand command, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ValidateOperation(employeeId, operationId);
        // Serialize as the base type so Kind participates in durable replay identity.
        var hash = Hash(new { caseId, expectedRevision, Command = JsonSerializer.Serialize<ReplacementCommand>(command) });
        return await TransactionAsync(async db =>
        {
            await LockOperationAsync(db, employeeId, operationId, cancellationToken);
            var replay = await ReplayAsync(db, employeeId, operationId, hash, cancellationToken);
            if (replay is not null) return replay;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(2147483100, {caseId})", cancellationToken);
            var row = await db.Set<ReplacementCaseRow>().SingleOrDefaultAsync(x => x.Id == caseId, cancellationToken)
                ?? throw new ReplacementStoreNotFoundException();
            if (row.Revision != expectedRevision) throw new ReplacementStoreConflictException("Case revision changed.");
            var current = Rehydrate(row, row.Revision);
            var next = command.Apply(current, employeeId, now);
            var facts = ReadFacts(row).Append(new(command, employeeId, now)).ToArray();
            row.CommandsJson = JsonSerializer.Serialize(facts); row.Revision = next.Revision;
            db.Add(Receipt(employeeId, operationId, hash, row.Id, next.Revision));
            await db.SaveChangesAsync(cancellationToken);
            return new ReplacementStoredCase(row.Id, next);
        }, cancellationToken);
    }

    public async Task<ReplacementStoredCase?> GetAsync(int caseId, CancellationToken cancellationToken)
    {
        await using var db = contexts();
        var row = await db.Set<ReplacementCaseRow>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == caseId, cancellationToken);
        return row is null ? null : new(row.Id, Rehydrate(row, row.Revision));
    }

    private async Task<ReplacementStoredCase> TransactionAsync(Func<DbContext, Task<ReplacementStoredCase>> action, CancellationToken cancellationToken)
    {
        await using var strategyContext = contexts();
        return await strategyContext.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var db = contexts();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await action(db);
            await transaction.CommitAsync(cancellationToken);
            return result;
        });
    }

    private static async Task<ReplacementStoredCase?> ReplayAsync(DbContext db, int employeeId, Guid operationId,
        string hash, CancellationToken cancellationToken)
    {
        var receipt = await db.Set<ReplacementOperationRow>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployeeId == employeeId && x.OperationId == operationId, cancellationToken);
        if (receipt is null) return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(receipt.PayloadHash), Encoding.ASCII.GetBytes(hash)))
            throw new ReplacementStoreConflictException("Operation identity was already used for another request.");
        var row = await db.Set<ReplacementCaseRow>().AsNoTracking().SingleAsync(x => x.Id == receipt.CaseId, cancellationToken);
        return new(row.Id, Rehydrate(row, receipt.ResultRevision));
    }

    private static ReplacementCase Rehydrate(ReplacementCaseRow row, int revision)
    {
        var value = ReplacementCase.Create(row.CustomerId, row.Reason,
            JsonSerializer.Deserialize<ReplacementOriginal[]>(row.OriginalsJson) ?? throw new InvalidOperationException("Invalid original snapshot."),
            JsonSerializer.Deserialize<ReplacementEvidence>(row.EvidenceJson) ?? throw new InvalidOperationException("Invalid report evidence."),
            row.ReportedBy, row.ReportedAt);
        var facts = ReadFacts(row);
        if (row.Revision != facts.Length + 1 || revision <= 0 || revision > row.Revision)
            throw new InvalidOperationException("Replacement revision does not reconcile with its history.");
        foreach (var fact in facts.Take(revision - 1)) value = fact.Command.Apply(value, fact.EmployeeId, fact.OccurredAt);
        return value;
    }
    private static ReplacementCommandFact[] ReadFacts(ReplacementCaseRow row) =>
        JsonSerializer.Deserialize<ReplacementCommandFact[]>(row.CommandsJson) ?? throw new InvalidOperationException("Invalid replacement history.");
    private static ReplacementOperationRow Receipt(int employeeId, Guid operationId, string hash, int caseId, int revision) =>
        new() { EmployeeId = employeeId, OperationId = operationId, PayloadHash = hash, CaseId = caseId, ResultRevision = revision };
    private static string Hash<T>(T input) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));
    private static void ValidateOperation(int employeeId, Guid operationId)
    {
        if (employeeId <= 0 || operationId == Guid.Empty) throw new ReplacementRuleException("Require actor-scoped stable operation identity.");
    }
    private static Task LockOperationAsync(DbContext db, int employeeId, Guid operationId, CancellationToken cancellationToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"replacement:{employeeId}:{operationId:D}"));
        var key = BinaryPrimitives.ReadInt64BigEndian(bytes);
        return db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }
}
