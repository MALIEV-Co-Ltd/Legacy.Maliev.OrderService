using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderModelCompatibilityTests { [Fact] public void Models_PreserveComputedColumnsAndSeparateStatusOwnership() { using var o = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql("Host=localhost;Database=o").Options); using var s = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql("Host=localhost;Database=s").Options); var e = o.Model.FindEntityType(typeof(Order))!; Assert.Contains("Manufactured", e.FindProperty(nameof(Order.Remaining))!.GetComputedColumnSql()); Assert.Contains("DiscountPercent", e.FindProperty(nameof(Order.Subtotal))!.GetComputedColumnSql()); Assert.Contains("FinishedDate", e.FindProperty(nameof(Order.Turnaround))!.GetComputedColumnSql()); Assert.True(e.FindProperty(nameof(Order.ModifiedDate))!.IsConcurrencyToken); Assert.Equal("OrderStatusHistory", s.Model.FindEntityType(typeof(OrderStatusHistory))!.GetTableName()); Assert.Null(s.Model.FindEntityType(typeof(Order))); } }
