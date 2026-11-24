namespace Legacy.Maliev.OrderService.Domain;

/// <summary>An order.</summary>
public sealed class Order
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the customer identifier.</summary>
    public int? CustomerId { get; set; }
    /// <summary>Gets or sets the employee identifier.</summary>
    public int? EmployeeId { get; set; }
    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets the description.</summary>
    public string? Description { get; set; }
    /// <summary>Gets or sets the process identifier.</summary>
    public int ProcessId { get; set; }
    /// <summary>Gets or sets the material identifier.</summary>
    public int? MaterialId { get; set; }
    /// <summary>Gets or sets the surface finish identifier.</summary>
    public int? SurfaceFinishId { get; set; }
    /// <summary>Gets or sets the color identifier.</summary>
    public int? ColorId { get; set; }
    /// <summary>Gets or sets the quantity.</summary>
    public int Quantity { get; set; }
    /// <summary>Gets or sets the manufactured quantity.</summary>
    public int Manufactured { get; set; }
    /// <summary>Gets the remaining quantity.</summary>
    public int? Remaining { get; private set; }
    public decimal? UnitPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? Subtotal { get; private set; }
    public int? CurrencyId { get; set; }
    /// <summary>Gets or sets the lead time.</summary>
    public int? LeadTime { get; set; }
    /// <summary>Gets or sets the promised date.</summary>
    public DateTime? PromisedDate { get; set; }
    /// <summary>Gets or sets the finished date.</summary>
    public DateTime? FinishedDate { get; set; }
    /// <summary>Gets the turnaround.</summary>
    public int? Turnaround { get; private set; }
    public string? Comment { get; set; }
    /// <summary>Gets or sets whether social media use is allowed.</summary>
    public bool AllowSocialMedia { get; set; }
    /// <summary>Gets or sets whether cancellation is allowed.</summary>
    public bool AllowCancellation { get; set; }
    public bool AllowPayment { get; set; }
    public string? TrackingNumber { get; set; }
    public string? OperationKey { get; set; }
    /// <summary>Gets or sets the created date.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modified date.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>Gets or sets the process.</summary>
    public Process? Process { get; set; }
    /// <summary>Gets the files for the order.</summary>
    public ICollection<OrderFile> Files { get; } = [];
}
/// <summary>A process.</summary>
public sealed class Process
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the category identifier.</summary>
    public int CategoryId { get; set; }
    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Gets or sets the created date.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modified date.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>Gets or sets the category.</summary>
    public Category? Category { get; set; }
    /// <summary>Gets the orders for the process.</summary>
    public ICollection<Order> Orders { get; } = [];
}
/// <summary>A category.</summary>
public sealed class Category
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets the created date.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modified date.</summary>
    public DateTime? ModifiedDate { get; set; }
    /// <summary>Gets the processes in the category.</summary>
    public ICollection<Process> Processes { get; } = [];
}
/// <summary>A file format.</summary>
public sealed class FileFormat
{
    /// <summary>Gets or sets the identifier.</summary>
    public int Id { get; set; }
    /// <summary>Gets or sets the name.</summary>
    public string? Name { get; set; }
    /// <summary>Gets or sets the extension.</summary>
    public string? Extension { get; set; }
    /// <summary>Gets or sets the created date.</summary>
    public DateTime? CreatedDate { get; set; }
    /// <summary>Gets or sets the modified date.</summary>
    public DateTime? ModifiedDate { get; set; }
}
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
