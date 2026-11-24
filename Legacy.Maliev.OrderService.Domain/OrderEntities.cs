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
    public string? OperationKey { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public Process? Process { get; set; }
    public ICollection<OrderFile> Files { get; } = [];
}
public sealed class Process { public int Id { get; set; } public int CategoryId { get; set; } public string Name { get; set; } = string.Empty; public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public Category? Category { get; set; } public ICollection<Order> Orders { get; } = []; }
public sealed class Category { public int Id { get; set; } public string? Name { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } public ICollection<Process> Processes { get; } = []; }
public sealed class FileFormat { public int Id { get; set; } public string? Name { get; set; } public string? Extension { get; set; } public DateTime? CreatedDate { get; set; } public DateTime? ModifiedDate { get; set; } }
/// <summary>Represents an object stored for an order.</summary>
public sealed class OrderFile
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the order identifier.</summary>
    public int OrderId { get; set; }
    /// <summary>Gets or sets the storage bucket.</summary>
    public string Bucket { get; set; } = string.Empty;
    /// <summary>Gets or sets the object name.</summary>
    public string ObjectName { get; set; } = string.Empty;
    /// <summary>Gets or sets the creation date.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modification date.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>Gets or sets the associated order.</summary>
    public Order? Order { get; set; }
}
/// <summary>An order status.</summary>
public sealed class OrderStatus
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the status name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the status description.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the creation date.</summary>
    public DateTime? CreatedDate { get; set; }

    /// <summary>Gets or sets the modification date.</summary>
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Gets the transitions originating at this status.</summary>
    public ICollection<OrderStatusTransition> FromTransitions { get; } = [];

    /// <summary>Gets the transitions ending at this status.</summary>
    public ICollection<OrderStatusTransition> ToTransitions { get; } = [];

    /// <summary>Gets the order history entries for this status.</summary>
    public ICollection<OrderStatusHistory> History { get; } = [];
}
/// <summary>An allowed transition between order statuses.</summary>
public sealed class OrderStatusTransition
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the originating status identifier.</summary>
    public int OrderStatusId { get; set; }

    /// <summary>Gets or sets the allowed destination status identifier.</summary>
    public int PossibleStatusId { get; set; }

    /// <summary>Gets or sets the creation date.</summary>
    public DateTime? CreatedDate { get; set; }

    /// <summary>Gets or sets the modification date.</summary>
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Gets or sets the originating status.</summary>
    public OrderStatus? OrderStatus { get; set; }

    /// <summary>Gets or sets the destination status.</summary>
    public OrderStatus? PossibleStatus { get; set; }
}
/// <summary>A recorded order status history entry.</summary>
public sealed class OrderStatusHistory
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the order identifier.</summary>
    public int OrderId { get; set; }

    /// <summary>Gets or sets the recorded status identifier.</summary>
    public int OrderStatusId { get; set; }

    /// <summary>Gets or sets the creation date.</summary>
    public DateTime? CreatedDate { get; set; }

    /// <summary>Gets or sets the modification date.</summary>
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Gets or sets the recorded status.</summary>
    public OrderStatus? OrderStatus { get; set; }
}
