#nullable disable
// Original 5fac CLR body; namespace relocated for isolated read-contract witness.
namespace Maliev.OrderService.Tests.SourceOrderReads
{
    using System;
    using System.Collections.Generic;

    public partial class OrderFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string Bucket { get; set; }
        public string ObjectName { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }

        public virtual Order Order { get; set; }
    }
}
