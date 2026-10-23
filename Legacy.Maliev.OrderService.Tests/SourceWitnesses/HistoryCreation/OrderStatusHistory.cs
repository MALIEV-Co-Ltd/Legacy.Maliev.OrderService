#nullable disable
// Original 5fac CLR body; namespace and encoding normalized only.
namespace Maliev.OrderService.Tests.SourceHistoryCreation
{
    using System;
    using System.Collections.Generic;

    public partial class OrderStatusHistory
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int OrderStatusId { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public OrderStatus OrderStatus { get; set; }
    }
}
