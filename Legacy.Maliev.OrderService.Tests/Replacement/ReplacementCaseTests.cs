using Legacy.Maliev.OrderService.Domain.Replacement;

namespace Legacy.Maliev.OrderService.Tests.Replacement;

public sealed class ReplacementCaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 6, 14, 22, TimeSpan.Zero);
    private static readonly ReplacementEvidence Evidence = new(Guid.NewGuid(), Guid.NewGuid());
    private static ReplacementOriginal Original(int quantity = 3, int orderId = 94826, int customerId = 69797) =>
        new(orderId, customerId, quantity, quantity, quantity, "LALAMOVE", new DateOnly(2026, 9, 30), 12, 34);
    private static ReplacementCase Case(int quantity = 3) => ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [Original(quantity)], Evidence, 7, Now);
    private static ReplacementCase Approved(int quantity = 3) => Case(quantity).Approve(ReturnDecision.Waived, false, false, "No return required", 8, Now);

    [Fact]
    public void Partial_replacement_preserves_original_snapshot_and_separate_counts()
    {
        var original = Original() with { Quantity = 10, Manufactured = 10, AffectedQuantity = 3 };
        var value = ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [original], Evidence, 7, Now)
            .Approve(ReturnDecision.Waived, false, false, "Carrier damage", 8, Now)
            .StartAttempt(94826, 3, 9, Now).CompleteQa(1, 3, 3, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 3, "Carrier", "NEW-TRACK", "Approved destination", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, true, Evidence, "Confirmed", 9, Now).Close(8, Now);
        Assert.Equal(original, value.Originals.Single());
        Assert.Equal("LALAMOVE", value.Originals.Single().TrackingNumber);
        Assert.Equal(10, value.Originals.Single().Manufactured);
        Assert.Equal(3, value.DeliveredQuantity(94826));
        Assert.Equal(ReplacementState.Closed, value.State);
        Assert.Equal("NEW-TRACK", value.Shipments.Single().TrackingNumber);
        Assert.Equal(new DateOnly(2026, 10, 8), value.Attempts.Single().FinishedDate);
    }

    [Fact]
    public void Mold_one_unit_can_be_replaced_without_touching_unrelated_new_order()
    {
        var value = Approved(1).StartAttempt(94826, 1, 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94876, 1, 9, Now));
        Assert.Single(value.Originals);
        Assert.Equal(1, value.Attempts.Single().Quantity);
    }

    [Fact]
    public void Partial_qa_failure_releases_only_unsuccessful_demand()
    {
        var value = Approved().StartAttempt(94826, 3, 9, Now)
            .CompleteQa(1, 3, 2, 1, new DateOnly(2026, 10, 8), Evidence, 10, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 2, 9, Now));
        value = value.StartAttempt(94826, 1, 9, Now);
        Assert.Equal(2, value.Attempts.Count);
        Assert.Equal(1, value.Attempts[0].RejectedQuantity);
    }

    [Fact]
    public void Partial_production_finishes_without_counting_unmade_units_as_scrap()
    {
        var value = Approved().StartAttempt(94826, 3, 9, Now)
            .CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .StartAttempt(94826, 2, 9, Now);
        Assert.Equal(1, value.Attempts[0].ProducedQuantity);
        Assert.Equal(0, value.Attempts[0].RejectedQuantity);
        Assert.Equal(2, value.Attempts[1].Quantity);
    }

    [Fact]
    public void Concurrent_candidate_attempts_cannot_reserve_more_than_open_quantity()
    {
        var value = Approved().StartAttempt(94826, 2, 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 2, 9, Now));
        Assert.Single(value.Attempts);
    }

    [Fact]
    public void Return_required_can_gate_production_and_shipment_independently()
    {
        var value = Case().Approve(ReturnDecision.Required, true, false, "Return before production", 8, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 1, 9, Now));
        value = value.RecordReturn(94826, 3, Evidence, "Received", 9, Now).StartAttempt(94826, 3, 9, Now);
        Assert.Single(value.Attempts);
        Assert.Throws<ReplacementRuleException>(() => value.RecordReturn(94826, 1, Evidence, "Extra", 9, Now));
    }

    [Fact]
    public void Shipment_gate_blocks_dispatch_but_allows_qa_before_return()
    {
        var value = Case(1).Approve(ReturnDecision.Required, false, true, "Return before dispatch", 8, Now)
            .StartAttempt(94826, 1, 9, Now).CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now);
        Assert.Throws<ReplacementRuleException>(() => value.Ship(1, 1, "Carrier", "TRACK", "Destination", new DateOnly(2026, 10, 8), 9, Now));
        value = value.RecordReturn(94826, 1, Evidence, "Received", 9, Now)
            .Ship(1, 1, "Carrier", "TRACK", "Destination", new DateOnly(2026, 10, 8), 9, Now);
        Assert.Single(value.Shipments);
    }

    [Fact]
    public void No_return_has_reason_but_no_fake_received_quantity()
    {
        var value = Approved();
        Assert.Empty(value.Returns);
        Assert.Throws<ReplacementRuleException>(() => value.RecordReturn(94826, 1, Evidence, "Fake", 9, Now));
        Assert.Throws<ReplacementRuleException>(() => Case().Approve(ReturnDecision.Waived, true, false, "Invalid gate", 8, Now));
        Assert.Throws<ReplacementRuleException>(() => Case().Approve(ReturnDecision.Waived, false, false, " ", 8, Now));
    }

    [Fact]
    public void Shipping_requires_qa_and_never_reuses_shipped_accepted_parts()
    {
        var value = Approved().StartAttempt(94826, 3, 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.Ship(1, 1, "Carrier", "T", "Dest", new DateOnly(2026, 10, 8), 9, Now));
        value = value.CompleteQa(1, 3, 3, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 2, "Carrier", "T1", "Dest", new DateOnly(2026, 10, 8), 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.Ship(1, 2, "Carrier", "T2", "Dest", new DateOnly(2026, 10, 8), 9, Now));
        value = value.Ship(1, 1, "Carrier", "T2", "Dest", new DateOnly(2026, 10, 8), 9, Now);
        Assert.Equal(2, value.Shipments.Count);
        Assert.Throws<ReplacementRuleException>(() => value.Close(8, Now));
    }

    [Fact]
    public void Failed_delivery_requires_explicit_repeat_authorization()
    {
        var value = Approved(1).StartAttempt(94826, 1, 9, Now)
            .CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 1, "Carrier", "T1", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, false, Evidence, "Lost", 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 1, 9, Now));
        value = value.AuthorizeRetry(1, "Remake after loss", 8, Now).StartAttempt(94826, 1, 9, Now);
        Assert.Equal(2, value.Attempts.Count);
        Assert.Equal(ShipmentOutcome.Failed, value.Shipments.Single().Outcome);
        Assert.Throws<ReplacementRuleException>(() => value.ConfirmDelivery(1, true, Evidence, "Rewrite", 9, Now));
        Assert.Throws<ReplacementRuleException>(() => value.AuthorizeRetry(1, "Again", 8, Now));
    }

    [Fact]
    public void Reject_is_terminal_without_original_order_side_effects()
    {
        var value = Case().Reject("Outside remedy scope", 8, Now);
        Assert.Equal(ReplacementState.Rejected, value.State);
        Assert.Throws<ReplacementRuleException>(() => value.Approve(ReturnDecision.Waived, false, false, "Reopen", 8, Now));
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 1, 9, Now));
    }

    [Fact]
    public void Audit_records_actor_time_and_revision_without_mutating_prior_version()
    {
        var previous = Case();
        var next = previous.Approve(ReturnDecision.Waived, false, false, "Approved", 8, Now);
        Assert.Equal(ReplacementState.Reported, previous.State);
        Assert.Equal(1, previous.Revision);
        Assert.Equal(2, next.Revision);
        Assert.Equal(8, next.Audit.Last().EmployeeId);
        Assert.Equal(Now, next.Audit.Last().OccurredAt);
        Assert.Equal("Approved", next.Audit.Last().Reason);
    }

    [Fact]
    public void Invalid_original_customer_duplicates_quantity_and_evidence_reject()
    {
        Assert.Throws<ReplacementRuleException>(() => ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [Original(customerId: 1)], Evidence, 7, Now));
        Assert.Throws<ReplacementRuleException>(() => ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [Original(), Original()], Evidence, 7, Now));
        Assert.Throws<ReplacementRuleException>(() => ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [Original() with { AffectedQuantity = 4 }], Evidence, 7, Now));
        Assert.Throws<ReplacementRuleException>(() => ReplacementCase.Create(69797, (ReplacementReason)99, [Original()], Evidence, 7, Now));
        Assert.Throws<ReplacementRuleException>(() => ReplacementCase.Create(69797, ReplacementReason.CarrierDamage, [Original()], new(Guid.Empty, Guid.NewGuid()), 7, Now));
        Assert.Throws<ReplacementRuleException>(() => Approved().StartAttempt(94826, 0, 9, Now));
        Assert.Throws<ReplacementRuleException>(() => Approved().StartAttempt(94826, int.MaxValue, 9, Now));
    }

    [Fact]
    public void Qa_rejects_inconsistent_counts_and_preproduction_finished_date()
    {
        var value = Approved().StartAttempt(94826, 3, 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.CompleteQa(1, 3, 3, 1, new DateOnly(2026, 10, 8), Evidence, 10, Now));
        Assert.Throws<ReplacementRuleException>(() => value.CompleteQa(1, 3, 3, 0, new DateOnly(2026, 10, 7), Evidence, 10, Now));
        Assert.Throws<ReplacementRuleException>(() => value.CompleteQa(1, 4, 4, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now));
    }

    [Fact]
    public void Dispatch_cannot_record_a_future_actual_shipment()
    {
        var value = Approved(1).StartAttempt(94826, 1, 9, Now)
            .CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now);
        Assert.Throws<ReplacementRuleException>(() => value.Ship(1, 1, "Carrier", "T", "Dest", new DateOnly(2026, 10, 9), 9, Now));
    }

    [Fact]
    public void Large_repeat_attempt_history_does_not_overflow_open_demand()
    {
        const int quantity = 1_500_000_000;
        var value = Approved(quantity).StartAttempt(94826, quantity, 9, Now)
            .CompleteQa(1, quantity, quantity, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, quantity, "Carrier", "T1", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, false, Evidence, "Lost", 9, Now)
            .AuthorizeRetry(1, "Remake", 8, Now).StartAttempt(94826, quantity, 9, Now)
            .CompleteQa(2, quantity, quantity, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 1, 9, Now));
        value = value.Ship(2, quantity, "Carrier", "T2", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(2, true, Evidence, "Confirmed", 9, Now).Close(8, Now);
        Assert.Equal(quantity, value.DeliveredQuantity(94826));
    }

    [Fact]
    public void Delivered_remedy_cannot_close_with_a_required_return_outstanding()
    {
        var value = Case(1).Approve(ReturnDecision.Required, false, false, "Return required", 8, Now)
            .StartAttempt(94826, 1, 9, Now).CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 1, "Carrier", "T", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, true, Evidence, "Confirmed", 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.Close(8, Now));
        value = value.WaiveRequiredReturn("Customer cannot return damaged part", 8, Now).Close(8, Now);
        Assert.Empty(value.Returns);
        Assert.Contains(value.Audit, x => x.Action == "ReturnWaived");
    }

    [Fact]
    public void Partial_delivery_plus_explicit_waiver_closes_without_more_production()
    {
        var value = Approved().StartAttempt(94826, 1, 9, Now)
            .CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 1, "Carrier", "T", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, true, Evidence, "Confirmed", 9, Now)
            .WaiveRemaining(94826, 2, "Customer accepted partial remedy", 8, Now);
        Assert.Equal(2, value.Waivers.Single().Quantity);
        Assert.Throws<ReplacementRuleException>(() => value.StartAttempt(94826, 1, 9, Now));
        Assert.Throws<ReplacementRuleException>(() => value.WaiveRemaining(94826, 1, "Over-waiver", 8, Now));
        Assert.Equal(ReplacementState.Closed, value.Close(8, Now).State);
    }

    [Fact]
    public void Failed_shipment_demand_requires_explicit_release_before_waiver()
    {
        var value = Approved(1).StartAttempt(94826, 1, 9, Now)
            .CompleteQa(1, 1, 1, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now)
            .Ship(1, 1, "Carrier", "Lost", "Dest", new DateOnly(2026, 10, 8), 9, Now)
            .ConfirmDelivery(1, false, Evidence, "Lost", 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.WaiveRemaining(94826, 1, "Cancel remedy", 8, Now));
        value = value.AuthorizeRetry(1, "Release failed shipment demand for decision", 8, Now)
            .WaiveRemaining(94826, 1, "Customer declined another remake", 8, Now).Close(8, Now);
        Assert.Equal(ReplacementState.Closed, value.State);
    }

    [Fact]
    public void Waiver_cannot_consume_active_production_or_accepted_unshipped_units()
    {
        var value = Approved().StartAttempt(94826, 2, 9, Now);
        Assert.Throws<ReplacementRuleException>(() => value.WaiveRemaining(94826, 2, "Too much", 8, Now));
        value = value.CompleteQa(1, 2, 2, 0, new DateOnly(2026, 10, 8), Evidence, 10, Now);
        Assert.Throws<ReplacementRuleException>(() => value.WaiveRemaining(94826, 2, "Too much", 8, Now));
        value = value.WaiveRemaining(94826, 1, "Agreed", 8, Now);
        Assert.Equal(1, value.Waivers.Single().Quantity);
    }
}
