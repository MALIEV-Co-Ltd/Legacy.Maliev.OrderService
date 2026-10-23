using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original latest status definition and its selected inverse history row.</summary>
/// <param name="Id">The stored definition identifier.</param>
/// <param name="Name">The definition name.</param>
/// <param name="Description">The definition description.</param>
/// <param name="CreatedDate">The definition creation date.</param>
/// <param name="ModifiedDate">The definition modification date.</param>
/// <param name="OrderStatusHistory">Only the selected highest-ID history row.</param>
public sealed record OrderLatestStatusReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate,
    [property: JsonPropertyName("orderStatusHistory")] IReadOnlyList<OrderLatestHistoryReadResponse> OrderStatusHistory)
{
    /// <summary>The empty outgoing transition collection from the original latest read.</summary>
    [JsonPropertyName("orderStatusHasPossibleStatusOrderStatus")]
    public IReadOnlyList<object> OrderStatusHasPossibleStatusOrderStatus { get; } = [];

    /// <summary>The empty incoming transition collection from the original latest read.</summary>
    [JsonPropertyName("orderStatusHasPossibleStatusPossibleStatus")]
    public IReadOnlyList<object> OrderStatusHasPossibleStatusPossibleStatus { get; } = [];

    /// <summary>Projects a consistent read snapshot without serializing a cyclic navigation.</summary>
    /// <param name="value">The definition and selected history from one database query.</param>
    /// <returns>The original latest-status wire representation.</returns>
    public static OrderLatestStatusReadResponse From(OrderLatestStatusReadResult value) =>
        new(value.Status.Id, value.Status.Name, value.Status.Description, value.Status.CreatedDate, value.Status.ModifiedDate,
            [new(value.History.Id, value.History.OrderId, value.History.OrderStatusId, value.History.CreatedDate, value.History.ModifiedDate)]);
}

/// <summary>The selected source history row without its cyclic status back-reference.</summary>
/// <param name="Id">The stored history identifier.</param>
/// <param name="OrderId">The requested order identifier.</param>
/// <param name="OrderStatusId">The stored definition identifier.</param>
/// <param name="CreatedDate">The history event creation date.</param>
/// <param name="ModifiedDate">The history event modification date.</param>
public sealed record OrderLatestHistoryReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("orderId")] int OrderId,
    [property: JsonPropertyName("orderStatusId")] int OrderStatusId,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate);
