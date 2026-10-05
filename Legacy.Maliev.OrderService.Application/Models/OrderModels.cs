using System.ComponentModel.DataAnnotations;
namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>An order snapshot with scalar service references and persisted computed values.</summary>
/// <param name="Id">Persistent order identifier.</param>
/// <param name="CustomerId">External customer scalar reference.</param>
/// <param name="EmployeeId">External employee scalar reference.</param>
/// <param name="Name">Order name.</param>
/// <param name="Description">Order description.</param>
/// <param name="ProcessId">Owned process identifier.</param>
/// <param name="MaterialId">External material scalar reference.</param>
/// <param name="SurfaceFinishId">External surface-finish scalar reference.</param>
/// <param name="ColorId">External color scalar reference.</param>
/// <param name="Quantity">Requested quantity.</param>
/// <param name="Manufactured">Manufactured quantity.</param>
/// <param name="Remaining">Persisted Quantity minus Manufactured.</param>
/// <param name="UnitPrice">Unit price.</param>
/// <param name="DiscountPercent">Discount percentage; null remains null.</param>
/// <param name="Subtotal">Persisted monetary subtotal at decimal precision 18,2.</param>
/// <param name="CurrencyId">External currency scalar reference.</param>
/// <param name="LeadTime">Quoted lead time.</param>
/// <param name="PromisedDate">Promised calendar date.</param>
/// <param name="FinishedDate">Finished calendar date.</param>
/// <param name="Turnaround">Calendar-day boundaries from CreatedDate to FinishedDate; null when unfinished.</param>
/// <param name="Comment">Order comment.</param>
/// <param name="AllowSocialMedia">Whether social media use is allowed.</param>
/// <param name="AllowCancellation">Whether cancellation is allowed subject to status rules.</param>
/// <param name="AllowPayment">Whether payment is allowed.</param>
/// <param name="TrackingNumber">Shipment tracking number.</param>
/// <param name="CreatedDate">Server creation timestamp.</param>
/// <param name="ModifiedDate">Server modification timestamp used for optimistic concurrency.</param>
public sealed record OrderResponse(int Id, int? CustomerId, int? EmployeeId, string? Name, string? Description, int ProcessId, int? MaterialId, int? SurfaceFinishId, int? ColorId, int Quantity, int Manufactured, int? Remaining, decimal? UnitPrice, decimal? DiscountPercent, decimal? Subtotal, int? CurrencyId, int? LeadTime, DateTime? PromisedDate, DateTime? FinishedDate, int? Turnaround, string? Comment, bool AllowSocialMedia, bool AllowCancellation, bool AllowPayment, string? TrackingNumber, DateTime? CreatedDate, DateTime? ModifiedDate);
/// <summary>Writable order fields; computed values and server timestamps are not accepted.</summary>
/// <param name="CustomerId">External customer scalar reference.</param>
/// <param name="EmployeeId">External employee scalar reference.</param>
/// <param name="Name">Order name.</param>
/// <param name="Description">Order description.</param>
/// <param name="ProcessId">Owned process identifier.</param>
/// <param name="MaterialId">External material scalar reference.</param>
/// <param name="SurfaceFinishId">External surface-finish scalar reference.</param>
/// <param name="ColorId">External color scalar reference.</param>
/// <param name="Quantity">Requested quantity.</param>
/// <param name="Manufactured">Manufactured quantity.</param>
/// <param name="UnitPrice">Unit price.</param>
/// <param name="DiscountPercent">Discount percentage; null remains null.</param>
/// <param name="CurrencyId">External currency scalar reference.</param>
/// <param name="LeadTime">Quoted lead time.</param>
/// <param name="PromisedDate">Promised calendar date.</param>
/// <param name="FinishedDate">Finished calendar date.</param>
/// <param name="Comment">Order comment.</param>
/// <param name="AllowSocialMedia">Whether social media use is allowed.</param>
/// <param name="AllowCancellation">Whether cancellation is allowed subject to status rules.</param>
/// <param name="AllowPayment">Whether payment is allowed.</param>
/// <param name="TrackingNumber">Shipment tracking number.</param>
/// <param name="OperationKey">Optional durable creation operation key; supplied consistently on retries.</param>
public sealed record UpsertOrderRequest(
    int? CustomerId,
    int? EmployeeId,
    [param: StringLength(100)] string? Name,
    [param: StringLength(250)] string? Description,
    int ProcessId,
    int? MaterialId,
    int? SurfaceFinishId,
    int? ColorId,
    int Quantity,
    int Manufactured,
    decimal? UnitPrice,
    decimal? DiscountPercent,
    int? CurrencyId,
    int? LeadTime,
    DateTime? PromisedDate,
    DateTime? FinishedDate,
    string? Comment,
    bool AllowSocialMedia,
    bool AllowCancellation,
    bool AllowPayment,
    string? TrackingNumber,
    [param: StringLength(128)] string? OperationKey = null);
public sealed record ProcessResponse(int Id, int CategoryId, string Name, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertProcessRequest(int CategoryId, string Name);
public sealed record CategoryResponse(int Id, string? Name, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertCategoryRequest(string? Name);
public sealed record FileFormatResponse(int Id, string? Name, string? Extension, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertFileFormatRequest(string? Name, string? Extension);
public sealed record OrderFileResponse(int Id, int OrderId, string Bucket, string ObjectName, DateTime? CreatedDate, DateTime? ModifiedDate);
/// <summary>Bucket and object metadata for an owned order; no object upload is performed.</summary>
/// <param name="OrderId">Optional owner identifier; omission preserves the existing owner.</param>
/// <param name="Bucket">Storage bucket name.</param>
/// <param name="ObjectName">Storage object name.</param>
public sealed record UpsertOrderFileRequest(int? OrderId, string Bucket, string ObjectName);
public sealed record OrderStatusResponse(int Id, string? Name, string? Description, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertOrderStatusRequest(string? Name, string? Description);
public sealed record OrderStatusHistoryResponse(int Id, int OrderId, int OrderStatusId, string? Name, string? Description, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertOrderStatusHistoryRequest(int OrderId, int OrderStatusId);
public sealed record CustomerOrderDetails(OrderResponse Order, ProcessResponse? Process, IReadOnlyList<OrderStatusHistoryResponse> History, IReadOnlyList<OrderFileResponse> Files);
public sealed record PaginatedResponse<T>(IReadOnlyList<T> Items, int PageIndex, int TotalPages, int TotalRecords) { public bool HasNextPage => PageIndex < TotalPages; public bool HasPreviousPage => PageIndex > 1; }
public enum OrderSortType
{
    OrderId_Ascending = 0,
    OrderId_Descending = 1,
    OrderCreatedDate_Ascending = 2,
    OrderCreatedDate_Descending = 3,
    OrderModifiedDate_Ascending = 4,
    OrderModifiedDate_Descending = 5,
    OrderStatus_Ascending = 6,
    OrderStatus_Descending = 7,
    OrderRemaining_Ascending = 8,
    OrderRemaining_Descending = 9,
    OrderQuantity_Ascending = 10,
    OrderQuantity_Descending = 11,
}
public enum UpdateResult { Updated, NotFound, Conflict, InvalidTransition }
