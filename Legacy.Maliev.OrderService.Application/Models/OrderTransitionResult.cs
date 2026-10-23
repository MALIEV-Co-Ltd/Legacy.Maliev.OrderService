namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>The transition outcome and event snapshot captured under its lifetime fence.</summary>
public sealed record OrderTransitionResult(UpdateResult Result, OrderHistoryCreation? History = null, bool StatusMissing = false);

/// <summary>The acknowledged event and its definition, independent of later history or definition edits.</summary>
public sealed record OrderHistoryCreation(int Id, int OrderId, int OrderStatusId, DateTime? CreatedDate,
    DateTime? ModifiedDate, OrderStatusResponse Status);
