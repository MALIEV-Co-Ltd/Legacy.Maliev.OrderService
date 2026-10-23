using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original created history graph with its tracked status definition.</summary>
/// <param name="Id">The acknowledged history identifier.</param>
/// <param name="OrderId">The owning order identifier.</param>
/// <param name="OrderStatusId">The status definition identifier.</param>
/// <param name="CreatedDate">The fresh UTC or retained stored creation date.</param>
/// <param name="ModifiedDate">The fresh UTC or retained stored modification date.</param>
/// <param name="OrderStatus">The definition captured with the acknowledged event.</param>
public sealed record OrderHistoryCreateResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("orderId")] int OrderId,
    [property: JsonPropertyName("orderStatusId")] int OrderStatusId,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate,
    [property: JsonPropertyName("orderStatus")] OrderStatusReadResponse OrderStatus)
{
    /// <summary>Maps the snapshot without reading a later event or changing stored date provenance.</summary>
    /// <param name="history">The snapshot captured by the transition.</param>
    /// <returns>The original HTTP representation.</returns>
    public static OrderHistoryCreateResponse From(OrderHistoryCreation history) =>
        new(history.Id, history.OrderId, history.OrderStatusId, history.CreatedDate,
            history.ModifiedDate, OrderStatusReadResponse.From(history.Status));
}
