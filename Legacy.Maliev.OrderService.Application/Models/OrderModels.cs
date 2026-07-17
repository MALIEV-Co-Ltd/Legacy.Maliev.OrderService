namespace Legacy.Maliev.OrderService.Application.Models;

public sealed record OrderResponse(int Id, int? CustomerId, int? EmployeeId, string? Name, string? Description, int ProcessId, int? MaterialId, int? SurfaceFinishId, int? ColorId, int Quantity, int Manufactured, int? Remaining, decimal? UnitPrice, decimal? DiscountPercent, decimal? Subtotal, int? CurrencyId, int? LeadTime, DateTime? PromisedDate, DateTime? FinishedDate, int? Turnaround, string? Comment, bool AllowSocialMedia, bool AllowCancellation, bool AllowPayment, string? TrackingNumber, DateTime? CreatedDate, DateTime? ModifiedDate);
public sealed record UpsertOrderRequest(int? CustomerId, int? EmployeeId, string? Name, string? Description, int ProcessId, int? MaterialId, int? SurfaceFinishId, int? ColorId, int Quantity, int Manufactured, decimal? UnitPrice, decimal? DiscountPercent, int? CurrencyId, int? LeadTime, DateTime? PromisedDate, DateTime? FinishedDate, string? Comment, bool AllowSocialMedia, bool AllowCancellation, bool AllowPayment, string? TrackingNumber);
public sealed record ProcessResponse(int Id, int CategoryId, string Name, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertProcessRequest(int CategoryId, string Name);
public sealed record CategoryResponse(int Id, string? Name, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertCategoryRequest(string? Name);
public sealed record FileFormatResponse(int Id, string? Name, string? Extension, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertFileFormatRequest(string? Name, string? Extension);
public sealed record OrderFileResponse(int Id, int OrderId, string Bucket, string ObjectName, DateTime? CreatedDate, DateTime? ModifiedDate); public sealed record UpsertOrderFileRequest(int? OrderId, string Bucket, string ObjectName);
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
