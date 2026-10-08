using Legacy.Maliev.OrderService.Application.Replacement;
using Legacy.Maliev.OrderService.Domain.Replacement;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

public sealed class ReplacementServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly ReplacementEvidence Evidence = new(Guid.NewGuid(), Guid.NewGuid());
    private static readonly ReplacementStoredCase Case = new(3, ReplacementCase.Create(42, ReplacementReason.CarrierDamage,
        [new(10, 42, 1, 1, 1, "Original", null, null, null)], Evidence, 7, Now));
    private readonly Mock<IReplacementRepository> repository = new();
    private readonly Mock<IReplacementAuthority> authority = new();
    private readonly Mock<IReplacementEvidenceVerifier> evidence = new();
    private ReplacementService Service(bool enabled = true) => new(repository.Object, authority.Object, evidence.Object, new(enabled), TimeProvider.System);

    public ReplacementServiceTests()
    {
        repository.Setup(x => x.GetAsync(3, It.IsAny<CancellationToken>())).ReturnsAsync(Case);
        repository.Setup(x => x.OriginalsMatchAsync(Case.Value, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted-tenant"));
    }

    [Fact]
    public async Task Disabled_module_never_reads_cases_or_authority()
    {
        await Assert.ThrowsAsync<ReplacementUnavailableException>(() => Service(false).GetAsync("staff", 3, default));
        repository.VerifyNoOtherCalls(); authority.VerifyNoOtherCalls(); evidence.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(ReplacementAuthorityOutcome.Denied)]
    [InlineData(ReplacementAuthorityOutcome.Unavailable)]
    public async Task Denied_or_unavailable_authority_never_mutates(ReplacementAuthorityOutcome outcome)
    {
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplacementAuthorityDecision(outcome));
        var error = await Record.ExceptionAsync(() => Service().ExecuteAsync("staff", 3, 1, Guid.NewGuid(), new ApproveReplacement(ReturnDecision.Waived, false, false, "Approve"), default));
        Assert.IsType(outcome == ReplacementAuthorityOutcome.Denied ? typeof(ReplacementDeniedException) : typeof(ReplacementUnavailableException), error);
        repository.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ReplacementCommand>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Theory]
    [InlineData(0, "tenant")]
    [InlineData(7, "")]
    public async Task Allowed_without_active_employee_or_trusted_tenant_is_unavailable(int employee, string tenant)
    {
        authority.Setup(x => x.AuthorizeAsync("staff", 42, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, employee, tenant));
        await Assert.ThrowsAsync<ReplacementUnavailableException>(() => Service().GetAsync("staff", 3, default));
    }
    [Fact]
    public async Task Lineage_is_owner_derived_with_exact_revision_and_case_local_ids()
    {
        var lineage = await Service().LineageAsync("staff", 3, default);
        Assert.Equal(3, lineage.CaseId); Assert.Equal(42, lineage.CustomerId); Assert.Equal([10], lineage.OriginalOrderIds);
        Assert.Equal(1, lineage.Revision); Assert.Empty(lineage.AttemptIds); Assert.Empty(lineage.ShipmentIds);
        authority.Verify(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Read, "/replacementcases/3", It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task Changed_original_customer_denies_read_and_command()
    {
        repository.Setup(x => x.OriginalsMatchAsync(Case.Value, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Service().LineageAsync("staff", 3, default));
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Service().ExecuteAsync("staff", 3, 1, Guid.NewGuid(), new StartReplacementAttempt(10, 1), default));
    }
    [Fact]
    public async Task Approval_requires_approval_permission_and_uses_authoritative_employee()
    {
        var command = new ApproveReplacement(ReturnDecision.Waived, false, false, "Approve"); var op = Guid.NewGuid();
        repository.Setup(x => x.ExecuteAsync(3, 1, op, 7, command, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(Case);
        await Service().ExecuteAsync("staff", 3, 1, op, command, default);
        authority.Verify(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Approve, "/replacementcases/3", It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task Qa_evidence_unavailable_prevents_command()
    {
        evidence.Setup(x => x.RequireAsync(42, It.IsAny<IReadOnlyList<int>>(), Evidence, "Evidence", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ReplacementUnavailableException());
        await Assert.ThrowsAsync<ReplacementUnavailableException>(() => Service().ExecuteAsync("staff", 3, 1, Guid.NewGuid(),
            new CompleteReplacementQa(1, 1, 1, 0, DateOnly.FromDateTime(Now.UtcDateTime), Evidence), default));
        repository.Verify(x => x.ExecuteAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<ReplacementCommand>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task Reconciliation_uses_authoritative_employee_and_current_original_ownership()
    {
        var operation = Guid.NewGuid(); repository.Setup(x => x.GetOperationAsync(7, operation, It.IsAny<CancellationToken>())).ReturnsAsync(Case);
        Assert.Equal(Case, await Service().OperationAsync("staff", 42, operation, default));
        repository.Setup(x => x.OriginalsMatchAsync(Case.Value, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Service().OperationAsync("staff", 42, operation, default));
        authority.Verify(x => x.AuthorizeAsync("staff", 42, ReplacementPermissions.Read, $"/customers/42/replacementcases/operations/{operation:D}", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
    [Fact]
    public async Task Operation_from_another_customer_denies_even_with_a_valid_employee_receipt()
    {
        var operation = Guid.NewGuid(); repository.Setup(x => x.GetOperationAsync(7, operation, It.IsAny<CancellationToken>())).ReturnsAsync(Case);
        authority.Setup(x => x.AuthorizeAsync("staff", 99, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ReplacementAuthorityDecision(ReplacementAuthorityOutcome.Allowed, 7, "trusted"));
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Service().OperationAsync("staff", 99, operation, default));
    }
    [Fact]
    public async Task List_requires_current_order_owner_and_checks_each_original()
    {
        repository.Setup(x => x.OriginalOrderMatchesAsync(42, 10, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        repository.Setup(x => x.ListAsync(42, 10, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Case });
        Assert.Single(await Service().ListAsync("staff", 42, 10, default));
        repository.Setup(x => x.OriginalOrderMatchesAsync(42, 10, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Assert.ThrowsAsync<ReplacementDeniedException>(() => Service().ListAsync("staff", 42, 10, default));
        repository.Verify(x => x.ListAsync(42, 10, It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task Duplicate_original_report_denies_before_evidence_or_storage()
    {
        await Assert.ThrowsAsync<ReplacementRuleException>(() => Service().ReportAsync("staff", Guid.NewGuid(), new(42, ReplacementReason.CarrierDamage, [new(10, 1), new(10, 1)], Evidence), default));
        evidence.VerifyNoOtherCalls(); repository.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dependency_transport_or_stream_failure_is_unavailable(bool stream)
    {
        repository.Setup(x => x.GetAsync(3, It.IsAny<CancellationToken>())).ThrowsAsync(stream ? new IOException("Interrupted protected stream") : new HttpRequestException("Owner unavailable"));
        await Assert.ThrowsAsync<ReplacementUnavailableException>(() => Service().GetAsync("staff", 3, default));
    }
    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_owner_failure()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        repository.Setup(x => x.GetAsync(3, It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException(cancelled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Service().GetAsync("staff", 3, cancelled.Token));
    }

}
