using System.Reflection;
using Moq;
using Legacy.Maliev.OrderService.Application.Replacement;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Data.Replacement;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

public sealed class ReplacementStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 14, 22, TimeSpan.Zero);
    private static readonly ReplacementEvidence Evidence = new(Guid.NewGuid(), Guid.NewGuid());
    private DbContext Db() => new FixtureContext(new DbContextOptionsBuilder<FixtureContext>().UseNpgsql(postgres.GetConnectionString()).Options);
    private ReplacementStore Store() => new(Db);
    public async Task InitializeAsync() { await postgres.StartAsync(); await using var db = Db(); await db.Database.EnsureCreatedAsync(); }
    public async Task DisposeAsync() => await postgres.DisposeAsync();

    private async Task<int> Seed(int customerId = 42)
    {
        await using var db = Db();
        var category = new Category { Name = "Manufacturing" };
        var process = new Process { Name = "Mold", Category = category };
        var order = new Order { CustomerId = customerId, Process = process, Quantity = 3, Manufactured = 3, TrackingNumber = "ORIGINAL", FinishedDate = new DateTime(2026, 9, 30), UnitPrice = 100, DiscountPercent = 0 };
        db.Add(order); await db.SaveChangesAsync(); return order.Id;
    }
    private async Task<ReplacementStoredCase> Create(int id, Guid? operation = null) =>
        await Store().CreateAsync(42, ReplacementReason.CarrierDamage, [new(id, 3)], Evidence, 7, operation ?? Guid.NewGuid(), Now, default);

    [Fact]
    public async Task Intake_reads_original_authority_and_preserves_all_original_fields()
    {
        var id = await Seed();
        await using var db = Db();
        var before = await db.Set<Order>().AsNoTracking().SingleAsync(x => x.Id == id);
        var created = await Create(id);
        Assert.Equal(42, created.Value.CustomerId);
        Assert.Equal("ORIGINAL", created.Value.Originals.Single().TrackingNumber);
        await Store().ExecuteAsync(created.Id, 1, Guid.NewGuid(), 8, new ApproveReplacement(ReturnDecision.Waived, false, false, "No return"), Now, default);
        db.ChangeTracker.Clear(); var after = await db.Set<Order>().AsNoTracking().SingleAsync(x => x.Id == id);
        Assert.Equal(before.Manufactured, after.Manufactured); Assert.Equal(before.Remaining, after.Remaining);
        Assert.Equal(before.FinishedDate, after.FinishedDate); Assert.Equal(before.TrackingNumber, after.TrackingNumber);
        Assert.Equal(before.Subtotal, after.Subtotal); Assert.Equal(before.ModifiedDate, after.ModifiedDate);
    }

    [Fact]
    public async Task Mixed_customer_and_missing_original_deny_before_case_write()
    {
        var id = await Seed(99);
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Create(id));
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Create(int.MaxValue));
        await using var db = Db();
        Assert.Empty(await db.Set<ReplacementCaseRow>().ToListAsync());
        Assert.Empty(await db.Set<ReplacementOperationRow>().ToListAsync());
    }

    [Fact]
    public async Task Lost_create_response_replays_exact_case_and_changed_payload_conflicts()
    {
        var id = await Seed(); var op = Guid.NewGuid(); var first = await Create(id, op); var replay = await Create(id, op);
        Assert.Equal(first.Id, replay.Id);
        await Assert.ThrowsAsync<ReplacementStoreConflictException>(() => Store().CreateAsync(42, ReplacementReason.ManufacturingNonconformance, [new(id, 3)], Evidence, 7, op, Now, default));
        await using var db = Db(); Assert.Single(await db.Set<ReplacementCaseRow>().ToListAsync());
    }

    [Fact]
    public async Task Command_replay_returns_original_result_after_later_changes()
    {
        var id = await Seed(); var value = await Create(id); var op = Guid.NewGuid();
        var command = new ApproveReplacement(ReturnDecision.Waived, false, false, "Approved");
        var approved = await Store().ExecuteAsync(value.Id, 1, op, 8, command, Now, default);
        await Store().ExecuteAsync(value.Id, 2, Guid.NewGuid(), 9, new StartReplacementAttempt(id, 2), Now, default);
        var replay = await Store().ExecuteAsync(value.Id, 1, op, 8, command, Now.AddSeconds(1), default);
        Assert.Equal(approved.Value.Revision, replay.Value.Revision);
        Assert.Empty(replay.Value.Attempts);
        await Assert.ThrowsAsync<ReplacementStoreConflictException>(() => Store().ExecuteAsync(value.Id, 1, op, 8, new RejectReplacement("Altered"), Now, default));
        Assert.Single((await Store().GetAsync(value.Id, default))!.Value.Attempts);
    }

    [Fact]
    public async Task Concurrent_same_revision_attempts_cannot_overreserve()
    {
        var id = await Seed(); var value = await Create(id);
        await Store().ExecuteAsync(value.Id, 1, Guid.NewGuid(), 8, new ApproveReplacement(ReturnDecision.Waived, false, false, "Approved"), Now, default);
        var first = Attempt(); var second = Attempt();
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x);
        var current = await Store().GetAsync(value.Id, default);
        Assert.Equal(2, current!.Value.Attempts.Single().Quantity);
        async Task<bool> Attempt()
        {
            try { await Store().ExecuteAsync(value.Id, 2, Guid.NewGuid(), 9, new StartReplacementAttempt(id, 2), Now, default); return true; }
            catch (ReplacementStoreConflictException) { return false; }
        }
    }

    [Fact]
    public async Task Rule_failure_rolls_back_state_and_operation_receipt()
    {
        var id = await Seed(); var value = await Create(id); var op = Guid.NewGuid();
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Store().ExecuteAsync(value.Id, 1, op, 9, new StartReplacementAttempt(id, 1), Now, default));
        Assert.Equal(1, (await Store().GetAsync(value.Id, default))!.Value.Revision);
        await using var db = Db(); Assert.False(await db.Set<ReplacementOperationRow>().AnyAsync(x => x.OperationId == op));
    }

    [Fact]
    public async Task Case_lineage_uses_persisted_customer_and_original_ids()
    {
        var id = await Seed(); var value = await Create(id);
        var read = await Store().GetAsync(value.Id, default);
        Assert.Equal(42, read!.Value.CustomerId);
        Assert.Equal([id], read.Value.Originals.Select(x => x.OrderId).ToArray());
        Assert.Null(await Store().GetAsync(int.MaxValue, default));
    }

    [Fact]
    public async Task Deleted_lifetime_is_denied_even_while_original_row_remains()
    {
        var id = await Seed(); await using var db = Db();
        db.Add(new OrderDeletionIntent { OrderId = id, DeletionId = Guid.NewGuid(), RequestedAtUtc = Now.UtcDateTime, NextAttemptAtUtc = Now.UtcDateTime });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Create(id));
        Assert.Empty(await db.Set<ReplacementCaseRow>().ToListAsync());
    }

    [Fact]
    public async Task Intake_waits_for_legacy_lifetime_fence_then_reads_new_customer_authority()
    {
        var id = await Seed(); await using var db = Db();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var identity = $"legacy-order-lifetime\n{id}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
        var intake = Create(id);
        var waiting = false;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!intake.IsCompleted && DateTime.UtcNow < deadline)
        {
            waiting = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity WHERE wait_event='advisory' AND pid<>pg_backend_pid()").SingleAsync() > 0;
            if (waiting) break;
            await Task.Delay(20);
        }
        Assert.True(waiting, "Intake never waited on the original-order lifetime fence.");
        Assert.False(intake.IsCompleted);
        await db.Set<Order>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerId, 99));
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<ReplacementRuleException>(() => intake);
        Assert.Empty(await db.Set<ReplacementCaseRow>().ToListAsync());
    }

    [Fact]
    public async Task Concurrent_open_cases_cannot_reserve_the_same_original_demand_twice()
    {
        var id = await Seed();
        await Store().CreateAsync(42, ReplacementReason.CarrierDamage, [new(id, 1)], Evidence, 7, Guid.NewGuid(), Now, default);
        await Store().CreateAsync(42, ReplacementReason.ManufacturingNonconformance, [new(id, 2)], Evidence, 7, Guid.NewGuid(), Now, default);
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Store().CreateAsync(42, ReplacementReason.CarrierDamage, [new(id, 1)], Evidence, 7, Guid.NewGuid(), Now, default));
        await using var db = Db(); Assert.Equal(2, await db.Set<ReplacementCaseRow>().CountAsync());
        Assert.Equal(3, (await db.Set<Order>().SingleAsync(x => x.Id == id)).Manufactured);
    }

    [Fact]
    public async Task Changed_original_customer_after_intake_denies_command_without_audit_or_receipt()
    {
        var id = await Seed(); var value = await Create(id); var operation = Guid.NewGuid();
        await using var db = Db();
        await db.Set<Order>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerId, 99));
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Store().ExecuteAsync(value.Id, 1, operation, 8,
            new ApproveReplacement(ReturnDecision.Waived, false, false, "Approve"), Now, default));
        Assert.Equal(1, (await Store().GetAsync(value.Id, default))!.Value.Revision);
        Assert.False(await db.Set<ReplacementOperationRow>().AnyAsync(x => x.OperationId == operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_rechecks_ownership_changed_during_evidence_verification(bool commandReplay)
    {
        var id = await Seed(); var reportOperation = Guid.NewGuid(); var value = await Create(id, reportOperation);
        var commandOperation = Guid.NewGuid(); var command = new ApproveReplacement(ReturnDecision.Waived, false, false, "Approved");
        if (commandReplay) await Store().ExecuteAsync(value.Id, 1, commandOperation, 7, command, Now, default);
        var authority = new Mock<IReplacementAuthority>();
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        var evidence = new Mock<IReplacementEvidenceVerifier>();
        evidence.Setup(x => x.RequireAsync(42, It.IsAny<IReadOnlyList<int>>(), Evidence, "Evidence", It.IsAny<CancellationToken>())).Returns(async () =>
        {
            // The service has read the current owner; simulate the concurrent canonical owner write
            // while its protected evidence RPC is outstanding, before the durable replay runs.
            await using var db = Db(); await using var tx = await db.Database.BeginTransactionAsync();
            var identity = $"legacy-order-lifetime\n{id}";
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({identity}, 0))");
            await db.Set<Order>().Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CustomerId, 99));
            await tx.CommitAsync();
        });
        var service = new ReplacementService(Store(), authority.Object, evidence.Object, new(true), TimeProvider.System);
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => commandReplay
            ? service.ExecuteAsync("staff", value.Id, 1, commandOperation, command, default)
            : service.ReportAsync("staff", reportOperation, new(42, ReplacementReason.CarrierDamage, [new(id, 3)], Evidence), default));
        await using var check = Db(); Assert.Equal(commandReplay ? 2 : 1, await check.Set<ReplacementOperationRow>().CountAsync());
    }

    private sealed class FixtureContext(DbContextOptions<FixtureContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            // Reuse the exact canonical original model, with only the new replacement configuration added.
            using var original = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().Options);
            typeof(OrderDbContext).GetMethod("OnModelCreating", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(original, [builder]);
            ReplacementModelConfiguration.Apply(builder);
        }
    }
}
