using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original status definition read response with empty navigation collections.</summary>
/// <param name="Id">The stored status definition identifier.</param>
/// <param name="Name">The status definition name.</param>
/// <param name="Description">The status definition description.</param>
/// <param name="CreatedDate">The definition creation date.</param>
/// <param name="ModifiedDate">The definition modification date.</param>
public sealed record OrderStatusReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate)
{
    /// <summary>The empty outgoing navigation collection from the original definition reads.</summary>
    [JsonPropertyName("orderStatusHasPossibleStatusOrderStatus")]
    public IReadOnlyList<object> OrderStatusHasPossibleStatusOrderStatus { get; } = [];

    /// <summary>The empty incoming navigation collection from the original definition reads.</summary>
    [JsonPropertyName("orderStatusHasPossibleStatusPossibleStatus")]
    public IReadOnlyList<object> OrderStatusHasPossibleStatusPossibleStatus { get; } = [];

    /// <summary>The empty history navigation collection from the original definition reads.</summary>
    [JsonPropertyName("orderStatusHistory")]
    public IReadOnlyList<object> OrderStatusHistory { get; } = [];

    /// <summary>Preserves a definition's scalar values at the HTTP read boundary.</summary>
    /// <param name="status">The unchanged internal definition response.</param>
    /// <returns>The original read wire representation.</returns>
    public static OrderStatusReadResponse From(OrderStatusResponse status) =>
        new(status.Id, status.Name, status.Description, status.CreatedDate, status.ModifiedDate);
}
