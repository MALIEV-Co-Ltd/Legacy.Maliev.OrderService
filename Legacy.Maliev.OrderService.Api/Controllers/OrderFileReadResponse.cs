using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Api.Controllers;

/// <summary>The original order-file wire representation, separate from internal DTO serialization.</summary>
public sealed record OrderFileReadResponse(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("orderId")] int OrderId,
    [property: JsonPropertyName("bucket")] string Bucket,
    [property: JsonPropertyName("objectName")] string ObjectName,
    [property: JsonPropertyName("createdDate")] DateTime? CreatedDate,
    [property: JsonPropertyName("modifiedDate")] DateTime? ModifiedDate,
    [property: JsonPropertyName("order")] OrderReadResponse? Order = null)
{
    public static OrderFileReadResponse From(OrderFileResponse value) =>
        new(value.Id, value.OrderId, value.Bucket, value.ObjectName, value.CreatedDate, value.ModifiedDate);

    public static OrderFileReadResponse FromCreated(OrderFileResponse value) =>
        new(value.Id, value.OrderId, value.Bucket, value.ObjectName,
            CreationDate(value.CreatedDate, value.IsFreshSourceCreation),
            CreationDate(value.ModifiedDate, value.IsFreshSourceCreation),
            value.SourceCreatedOrder is { } parent ? OrderReadResponse.From(parent) : null);

    private static DateTime? CreationDate(DateTime? value, bool fresh) =>
        fresh && value is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : value;
}
