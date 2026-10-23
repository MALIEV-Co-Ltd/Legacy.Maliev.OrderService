using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original Order page envelope with its source entity representations.</summary>
/// <param name="Items">The selected order snapshots.</param>
/// <param name="PageIndex">The admitted page index.</param>
/// <param name="TotalPages">The existing total page count.</param>
/// <param name="TotalRecords">The existing matching record count.</param>
public sealed record OrderPageReadResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<OrderReadResponse> Items,
    [property: JsonPropertyName("pageIndex")] int PageIndex,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("totalRecords")] int TotalRecords)
{
    /// <summary>Whether a later page exists.</summary>
    [JsonPropertyName("hasNextPage")]
    public bool HasNextPage => PageIndex < TotalPages;

    /// <summary>Whether an earlier page exists.</summary>
    [JsonPropertyName("hasPreviousPage")]
    public bool HasPreviousPage => PageIndex > 1;

    /// <summary>Projects the existing selected page without querying or changing pagination.</summary>
    /// <param name="value">The internal page projection.</param>
    /// <returns>The original page response.</returns>
    public static OrderPageReadResponse From(PaginatedResponse<OrderResponse> value) =>
        new(value.Items.Select(OrderReadResponse.From).ToArray(), value.PageIndex, value.TotalPages, value.TotalRecords);
}
