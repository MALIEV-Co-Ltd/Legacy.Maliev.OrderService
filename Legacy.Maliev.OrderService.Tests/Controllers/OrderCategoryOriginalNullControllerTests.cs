using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderCategoryOriginalNullControllerTests
{
    [Fact]
    public async Task CreateCategoryAsync_NullRequest_ReturnsEmptyBadRequestBeforeServiceAccess()
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new CategoriesController(service.Object);

        var result = await controller.CreateCategoryAsync(null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task UpdateCategoryAsync_NullRequest_ReturnsEmptyBadRequestBeforeLookup(int id)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new CategoriesController(service.Object);

        var result = await controller.UpdateCategoryAsync(id, null!, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
    }
}
