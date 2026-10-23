using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original process GET representation without cyclic navigations.</summary>
/// <param name="Id">The stored process identifier.</param>
/// <param name="CategoryId">The stored category identifier.</param>
/// <param name="Name">The process name.</param>
/// <param name="CreatedDate">The process creation date.</param>
/// <param name="ModifiedDate">The process modification date.</param>
/// <param name="Category">The category loaded only by filtered reads.</param>
public sealed record ProcessReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("categoryId")] int CategoryId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate,
    [property: JsonPropertyName("category")] ProcessCategoryReadResponse? Category)
{
    /// <summary>The original unloaded order collection.</summary>
    [JsonPropertyName("order")]
    public IReadOnlyList<object> Order { get; } = [];

    /// <summary>Projects the source wire shape from a consistent read snapshot.</summary>
    /// <param name="value">The process and its optionally loaded category.</param>
    /// <returns>The original process GET representation.</returns>
    public static ProcessReadResponse From(ProcessReadResult value) =>
        new(value.Process.Id, value.Process.CategoryId, value.Process.Name, value.Process.CreatedDate, value.Process.ModifiedDate,
            value.Category is { } category ? new(category.Id, category.Name, category.CreatedDate, category.ModifiedDate) : null);
}

/// <summary>The category loaded by the original no-tracking filtered process query.</summary>
/// <param name="Id">The stored category identifier.</param>
/// <param name="Name">The category name.</param>
/// <param name="CreatedDate">The category creation date.</param>
/// <param name="ModifiedDate">The category modification date.</param>
public sealed record ProcessCategoryReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate)
{
    /// <summary>The source serializer omits the sole inverse process because it is the containing row.</summary>
    [JsonPropertyName("process")]
    public IReadOnlyList<object> Process { get; } = [];
}
