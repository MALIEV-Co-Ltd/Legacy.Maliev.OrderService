using System.ComponentModel.DataAnnotations;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderModelCompatibilityTests
{
    [Fact]
    public void Models_PreserveComputedColumnsAndSeparateStatusOwnership()
    {
        using var o = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql("Host=localhost;Database=o").Options);
        using var s = new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql("Host=localhost;Database=s").Options);
        var e = o.Model.FindEntityType(typeof(Order))!;
        Assert.Contains("Manufactured", e.FindProperty(nameof(Order.Remaining))!.GetComputedColumnSql());
        Assert.Contains("DiscountPercent", e.FindProperty(nameof(Order.Subtotal))!.GetComputedColumnSql());
        Assert.Contains("FinishedDate", e.FindProperty(nameof(Order.Turnaround))!.GetComputedColumnSql());
        Assert.True(e.FindProperty(nameof(Order.ModifiedDate))!.IsConcurrencyToken);
        Assert.Equal("OrderStatusHistory", s.Model.FindEntityType(typeof(OrderStatusHistory))!.GetTableName());
        Assert.Null(s.Model.FindEntityType(typeof(Order)));
    }

    [Theory]
    [InlineData(101, 1, nameof(UpsertOrderRequest.Name))]
    [InlineData(1, 251, nameof(UpsertOrderRequest.Description))]
    public void UpsertOrderRequest_RejectsTextBeyondPersistedColumnLimits(
        int nameLength,
        int descriptionLength,
        string expectedMember)
    {
        var request = Request(new string('N', nameLength), new string('D', descriptionLength));
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(request, new ValidationContext(request), results, true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(expectedMember));
    }

    private static UpsertOrderRequest Request(string name, string description) => new(
        CustomerId: 42,
        EmployeeId: null,
        Name: name,
        Description: description,
        ProcessId: 1,
        MaterialId: null,
        SurfaceFinishId: null,
        ColorId: null,
        Quantity: 1,
        Manufactured: 0,
        UnitPrice: 100m,
        DiscountPercent: 0m,
        CurrencyId: 764,
        LeadTime: 3,
        PromisedDate: null,
        FinishedDate: null,
        Comment: null,
        AllowSocialMedia: false,
        AllowCancellation: true,
        AllowPayment: true,
        TrackingNumber: null);
}
