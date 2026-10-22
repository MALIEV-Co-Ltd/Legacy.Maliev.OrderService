using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderCatalogOriginalNullControllerTests
{
    [Fact]
    public async Task CreateFileFormatAsync_NullRequest_ReturnsEmptyBadRequestBeforeServiceAccess()
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new FileFormatsController(service.Object);

        var result = await controller.CreateFileFormatAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task UpdateFileFormatAsync_NullRequest_ReturnsEmptyBadRequestBeforeLookup(int id)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new FileFormatsController(service.Object);

        var result = await controller.UpdateFileFormatAsync(id, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateProcessAsync_NullRequest_ReturnsEmptyBadRequestBeforeServiceAccess()
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new ProcessesController(service.Object);

        var result = await controller.CreateProcessAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task UpdateProcessAsync_NullRequest_ReturnsEmptyBadRequestBeforeLookup(int id)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new ProcessesController(service.Object);

        var result = await controller.UpdateProcessAsync(id, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateOrderStatusAsync_NullRequest_ReturnsEmptyBadRequestBeforeServiceAccess()
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new OrderStatusesController(service.Object);

        var result = await controller.CreateOrderStatusAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task UpdateOrderStatusAsync_NullRequest_ReturnsEmptyBadRequestBeforeLookup(int id)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new OrderStatusesController(service.Object);

        var result = await controller.UpdateOrderStatusAsync(id, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }
}
