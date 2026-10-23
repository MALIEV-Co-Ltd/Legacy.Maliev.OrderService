#nullable disable
// Original 5fac source CLR body, without encoding marker.
namespace Maliev.OrderService.Tests.SourceOrderFiles
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
