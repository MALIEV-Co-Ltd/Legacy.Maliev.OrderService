#nullable disable
// Original 5fac Category CLR body; only the nullable compiler context is adapted.
namespace Maliev.OrderService.Data.Database.OrderContext
{
    using System;
    using System.Collections.Generic;

    public partial class Category
    {
        public Category()
        {
            this.Process = new HashSet<Process>();
        }

        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public virtual ICollection<Process> Process { get; set; }
    }
}
