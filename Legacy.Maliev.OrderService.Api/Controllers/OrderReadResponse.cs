using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original order scalar wire representation with unloaded related entities.</summary>
public sealed record OrderReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("customerId")] int? CustomerId,
    [property: JsonPropertyName("employeeId")] int? EmployeeId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("materialId")] int? MaterialId,
    [property: JsonPropertyName("surfaceFinishId")] int? SurfaceFinishId,
    [property: JsonPropertyName("colorId")] int? ColorId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("manufactured")] int Manufactured,
    [property: JsonPropertyName("remaining")] int? Remaining,
    [property: JsonPropertyName("unitPrice")] decimal? UnitPrice,
    [property: JsonPropertyName("discountPercent")] decimal? DiscountPercent,
    [property: JsonPropertyName("subtotal")] decimal? Subtotal,
    [property: JsonPropertyName("currencyId")] int? CurrencyId,
    [property: JsonPropertyName("leadTime")] int? LeadTime,
    [property: JsonPropertyName("promisedDate")] DateTime? PromisedDate,
    [property: JsonPropertyName("finishedDate")] DateTime? FinishedDate,
    [property: JsonPropertyName("turnaround")] int? Turnaround,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("allowSocialMedia")] bool AllowSocialMedia,
    [property: JsonPropertyName("allowCancellation")] bool AllowCancellation,
    [property: JsonPropertyName("allowPayment")] bool AllowPayment,
    [property: JsonPropertyName("trackingNumber")] string? TrackingNumber,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate)
{
    // Unloaded orders and fresh file reference-loop omission both leave this collection empty.
    [JsonPropertyName("orderFile")] public IReadOnlyList<object> OrderFile { get; } = [];

    /// <summary>Uses fresh insert dates only when the repository confirms a new row was created.</summary>
    public static OrderReadResponse FromCreated(OrderResponse value) => value.SourceCreation is { } source
        ? From(value) with
        {
            PromisedDate = source.PromisedDate,
            FinishedDate = source.FinishedDate,
            CreatedDate = source.CreatedDate,
            ModifiedDate = source.CreatedDate,
        }
        : From(value);

    public static OrderReadResponse From(OrderResponse value) => new(
        value.Id, value.CustomerId, value.EmployeeId, value.Name, value.Description, value.ProcessId,
        value.MaterialId, value.SurfaceFinishId, value.ColorId, value.Quantity, value.Manufactured,
        value.Remaining, value.UnitPrice, value.DiscountPercent, value.Subtotal, value.CurrencyId,
        value.LeadTime, value.PromisedDate, value.FinishedDate, value.Turnaround, value.Comment,
        value.AllowSocialMedia, value.AllowCancellation, value.AllowPayment, value.TrackingNumber,
        value.CreatedDate, value.ModifiedDate);
}
