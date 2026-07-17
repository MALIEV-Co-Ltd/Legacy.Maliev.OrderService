using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Tests.Contracts;

public sealed class OrderSortContractTests
{
    [Theory]
    [InlineData("OrderId_Ascending", 0)]
    [InlineData("OrderId_Descending", 1)]
    [InlineData("OrderCreatedDate_Ascending", 2)]
    [InlineData("OrderCreatedDate_Descending", 3)]
    [InlineData("OrderModifiedDate_Ascending", 4)]
    [InlineData("OrderModifiedDate_Descending", 5)]
    [InlineData("OrderStatus_Ascending", 6)]
    [InlineData("OrderStatus_Descending", 7)]
    [InlineData("OrderRemaining_Ascending", 8)]
    [InlineData("OrderRemaining_Descending", 9)]
    [InlineData("OrderQuantity_Ascending", 10)]
    [InlineData("OrderQuantity_Descending", 11)]
    public void LegacySortNames_PreserveExactNumericWireValues(string name, int value)
    {
        Assert.Equal(12, Enum.GetNames<OrderSortType>().Length);
        Assert.Equal(value, (int)Enum.Parse<OrderSortType>(name));
    }
}
