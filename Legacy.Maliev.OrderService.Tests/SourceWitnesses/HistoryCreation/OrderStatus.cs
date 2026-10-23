#nullable disable
// Original 5fac CLR body; namespace and encoding normalized only.
namespace Maliev.OrderService.Tests.SourceHistoryCreation
{
    using System;
    using System.Collections.Generic;

    public partial class OrderStatus
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
