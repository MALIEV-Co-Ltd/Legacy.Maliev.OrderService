#nullable disable
// Original 5fac Process CLR body. These witnesses require Order to remain empty.
using Order = System.Object;

namespace Maliev.OrderService.Data.Database.OrderContext
{
    using System;
    using System.Collections.Generic;

    public partial class Process
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

        public virtual Category Category { get; set; }
        public virtual ICollection<Order> Order { get; set; }
    }
}
