#nullable disable
// Original 5fac CLR body; namespace and encoding normalized only.
namespace Maliev.OrderService.Tests.SourceHistoryCreation
{
    using System;
    using System.Collections.Generic;

    public partial class OrderStatusHasPossibleStatus
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
