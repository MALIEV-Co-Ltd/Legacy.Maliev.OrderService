using System.Text.Json.Serialization;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original status-shaped history response with a display ordinal and event dates.</summary>
/// <param name="Id">The one-based chronological display ordinal.</param>
/// <param name="Name">The status definition name.</param>
/// <param name="Description">The status definition description.</param>
/// <param name="CreatedDate">The history event creation date.</param>
/// <param name="ModifiedDate">The history event modification date.</param>
public sealed record OrderHistoryStatusResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate);
