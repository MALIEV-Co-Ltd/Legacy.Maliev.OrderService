#nullable disable
// Original 5fac CLR body; namespace relocated for isolated read-contract witness.
// These read routes never load Process.
using Process = System.Object;
namespace Maliev.OrderService.Tests.SourceOrderReads
{
    using System;
    using System.Collections.Generic;

    public partial class Order
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

        public virtual Process Process { get; set; }
        public virtual ICollection<OrderFile> OrderFile { get; set; }
    }
}
