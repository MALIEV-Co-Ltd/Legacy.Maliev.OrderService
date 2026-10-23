namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>The definition and selected history row from one latest-status read snapshot.</summary>
/// <param name="Status">The unchanged internal status definition response.</param>
/// <param name="History">The unchanged internal history response for the selected row.</param>
public sealed record OrderLatestStatusReadResult(OrderStatusResponse Status, OrderStatusHistoryResponse History);
