namespace Legacy.Maliev.OrderService.Domain;

public sealed class Order
{
    public int Id { get; set; }
    public int? CustomerId { get; set; }
    public int? EmployeeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ProcessId { get; set; }
    public int? MaterialId { get; set; }
    public int? SurfaceFinishId { get; set; }
    public int? ColorId { get; set; }
    public int Quantity { get; set; }
    public int Manufactured { get; set; }
    public int? Remaining { get; private set; }
    public decimal? UnitPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? Subtotal { get; private set; }
    public int? CurrencyId { get; set; }
    public int? LeadTime { get; set; }
    public DateTime? PromisedDate { get; set; }
    public DateTime? FinishedDate { get; set; }
    public int? Turnaround { get; private set; }
    public string? Comment { get; set; }
    public bool AllowSocialMedia { get; set; }
    public bool AllowCancellation { get; set; }
    public bool AllowPayment { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public Process? Process { get; set; }
    public ICollection<OrderFile> Files { get; } = [];
}
public sealed class Process { public int Id { get; set; } public int CategoryId { get; set; } public string Name { get; set; } = string.Empty; public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public Category? Category { get; set; } public ICollection<Order> Orders { get; } = []; }
public sealed class Category { public int Id { get; set; } public string? Name { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public ICollection<Process> Processes { get; } = []; }
public sealed class FileFormat { public int Id { get; set; } public string? Name { get; set; } public string? Extension { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } }
public sealed class OrderFile { public int Id { get; set; } public int OrderId { get; set; } public string Bucket { get; set; } = string.Empty; public string ObjectName { get; set; } = string.Empty; public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public Order? Order { get; set; } }
public sealed class OrderStatus { public int Id { get; set; } public string? Name { get; set; } public string? Description { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public ICollection<OrderStatusTransition> FromTransitions { get; } = []; public ICollection<OrderStatusTransition> ToTransitions { get; } = []; public ICollection<OrderStatusHistory> History { get; } = []; }
public sealed class OrderStatusTransition { public int Id { get; set; } public int OrderStatusId { get; set; } public int PossibleStatusId { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public OrderStatus? OrderStatus { get; set; } public OrderStatus? PossibleStatus { get; set; } }
public sealed class OrderStatusHistory { public int Id { get; set; } public int OrderId { get; set; } public int OrderStatusId { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public OrderStatus? OrderStatus { get; set; } }
