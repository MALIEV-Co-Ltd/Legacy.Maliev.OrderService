#nullable disable
// Original 5fac CLR body; only the nullable compiler context is adapted.
namespace Maliev.OrderService.Data.Database.OrderContext
{
    using System;
    using System.Collections.Generic;

    public partial class FileFormat
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Extension { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }
}
