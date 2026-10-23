using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original Category representation with its unloaded process collection.</summary>
/// <param name="Id">The stored category identifier.</param>
/// <param name="Name">The category name.</param>
/// <param name="CreatedDate">The category creation date.</param>
/// <param name="ModifiedDate">The category modification date.</param>
public sealed record CategoryReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate)
{
    /// <summary>The original fresh Category's unloaded process collection.</summary>
    [JsonPropertyName("process")]
    public IReadOnlyList<object> Process { get; } = [];

    /// <summary>Projects a stored Category without changing its date representation.</summary>
    /// <param name="value">The internal category projection.</param>
    /// <returns>The original Category GET representation.</returns>
    public static CategoryReadResponse From(CategoryResponse value) =>
        new(value.Id, value.Name, value.CreatedDate, value.ModifiedDate);

    /// <summary>Preserves the original newly constructed entity's UTC server-date representation.</summary>
    /// <param name="value">The internal newly created category projection.</param>
    /// <returns>The original Category POST representation.</returns>
    public static CategoryReadResponse FromCreated(CategoryResponse value) =>
        new(value.Id, value.Name,
            value.CreatedDate is { } created ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : null,
            value.ModifiedDate is { } modified ? DateTime.SpecifyKind(modified, DateTimeKind.Utc) : null);
}
