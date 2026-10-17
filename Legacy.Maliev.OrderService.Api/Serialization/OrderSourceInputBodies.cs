#nullable disable
// Inert original input shapes: no EF tracking, DTO validation or output registration.
// Order is navigation-only; its root UpsertOrderRequest actions are not selected.
namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext
{
    internal sealed class Category
    {
        public Category()
        {
            this.Process = new HashSet<Process>();
        }

        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public ICollection<Process> Process { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext
{
    internal sealed class FileFormat
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Extension { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext
{
    internal sealed class Process
    {
        public Process()
        {
            this.Order = new HashSet<Order>();
        }

        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public Category Category { get; set; }
        public ICollection<Order> Order { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext
{
    internal sealed class OrderFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Bucket { get; set; }
        public string ObjectName { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public Order Order { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext
{
    internal sealed class Order
    {
        public Order()
        {
            this.OrderFile = new HashSet<OrderFile>();
        }

        public int Id { get; set; }
        public int? CustomerId { get; set; }
        public int? EmployeeId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public int ProcessId { get; set; }
        public int? MaterialId { get; set; }
        public int? SurfaceFinishId { get; set; }
        public int? ColorId { get; set; }
        public int Quantity { get; set; }
        public int Manufactured { get; set; }
        public int? Remaining { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? DiscountPercent { get; set; }
        public decimal? Subtotal { get; set; }
        public int? CurrencyId { get; set; }
        public int? LeadTime { get; set; }
        public DateTime? PromisedDate { get; set; }
        public DateTime? FinishedDate { get; set; }
        public int? Turnaround { get; set; }
        public string Comment { get; set; }
        public bool AllowSocialMedia { get; set; }
        public bool AllowCancellation { get; set; }
        public bool AllowPayment { get; set; }
        public string TrackingNumber { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public Process Process { get; set; }
        public ICollection<OrderFile> OrderFile { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderStatusContext
{
    internal sealed class OrderStatus
    {
        public OrderStatus()
        {
            this.OrderStatusHasPossibleStatusOrderStatus = new HashSet<OrderStatusHasPossibleStatus>();
            this.OrderStatusHasPossibleStatusPossibleStatus = new HashSet<OrderStatusHasPossibleStatus>();
            this.OrderStatusHistory = new HashSet<OrderStatusHistory>();
        }

        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public ICollection<OrderStatusHasPossibleStatus> OrderStatusHasPossibleStatusOrderStatus { get; set; }
        public ICollection<OrderStatusHasPossibleStatus> OrderStatusHasPossibleStatusPossibleStatus { get; set; }
        public ICollection<OrderStatusHistory> OrderStatusHistory { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderStatusContext
{
    internal sealed class OrderStatusHistory
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int OrderStatusId { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public OrderStatus OrderStatus { get; set; }
    }
}

namespace Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderStatusContext
{
    internal sealed class OrderStatusHasPossibleStatus
    {
        public int Id { get; set; }
        public int OrderStatusId { get; set; }
        public int PossibleStatusId { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public OrderStatus OrderStatus { get; set; }
        public OrderStatus PossibleStatus { get; set; }
    }
}

#nullable restore
