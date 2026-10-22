using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileHistoryOriginalNullControllerTests
{
    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task UpdateOrderFileAsync_NullRequest_ReturnsOriginalBadRequestBeforeLookup(int id)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new FilesController(service.Object);

        var result = await controller.UpdateOrderFileAsync(id, null!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal("item is required.", Assert.IsType<string>(badRequest.Value));
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(101, true)]
    [InlineData(int.MaxValue, true)]
    public async Task UpdateOrderHistoryAsync_NullRequest_ReturnsEmptyBadRequestBeforeLookup(int id, bool hasExpectedDate)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var controller = new HistoriesController(service.Object, idempotency.Object);
        DateTimeOffset? expected = hasExpectedDate ? new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero) : null;

        var result = await controller.UpdateOrderHistoryAsync(id, null!, expected, CancellationToken.None);

        Assert.Equal(400, Assert.IsType<BadRequestResult>(result).StatusCode);
        service.VerifyNoOtherCalls();
        idempotency.VerifyNoOtherCalls();
    }
}
