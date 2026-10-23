using Legacy.Maliev.OrderService.Api.Controllers;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderFileCreateOriginalResponseTests
{
    [Theory]
    [InlineData(null, null, "bucket is required")]
    [InlineData(null, "part.step", "bucket is required")]
    [InlineData("", "part.step", "bucket is required")]
    [InlineData("metadata", null, "objectName is required")]
    [InlineData("metadata", "", "objectName is required")]
    [InlineData("", "", "bucket is required")]
    [InlineData(" ", "", "objectName is required")]
    public async Task EmptyArguments_ReturnOriginalOrderedMessageBeforeServiceAccess(string? bucket, string? objectName, string expected)
    {
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        var controller = new FilesController(service.Object);

        var result = await controller.CreateOrderFileEntryAsync(int.MaxValue, bucket!, objectName!, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(400, badRequest.StatusCode);
        Assert.Equal(expected, Assert.IsType<string>(badRequest.Value));
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(" ", "part.step")]
    [InlineData("metadata", " ")]
    [InlineData(" metadata ", " drawings/part ")]
    public async Task NonemptyLiteralArguments_PreserveSourceDelegationAndCreatedResult(string bucket, string objectName)
    {
        const int id = 101;
        var file = new OrderFileResponse(501, id, bucket, objectName, null, null);
        using var cancellation = new CancellationTokenSource();
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        service.Setup(value => value.CreateFileAsync(id, bucket, objectName, cancellation.Token)).ReturnsAsync(file);
        var controller = new FilesController(service.Object);

        var result = await controller.CreateOrderFileEntryAsync(id, bucket, objectName, cancellation.Token);

        var created = Assert.IsType<CreatedAtRouteResult>(result);
        Assert.Equal("GetOrderFile", created.RouteName);
        Assert.Equal(file.Id, created.RouteValues!["id"]);
        var response = Assert.IsType<OrderFileReadResponse>(created.Value);
        Assert.Equal(new OrderFileReadResponse(file.Id, file.OrderId, file.Bucket, file.ObjectName,
            file.CreatedDate, file.ModifiedDate), response);
        service.Verify(value => value.CreateFileAsync(id, bucket, objectName, cancellation.Token), Times.Once);
        service.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public async Task MissingParent_ReturnsOriginalLiteralAndPreservesToken(int id)
    {
        using var cancellation = new CancellationTokenSource();
        var service = new Mock<IOrderService>(MockBehavior.Strict);
        service.Setup(value => value.CreateFileAsync(id, "metadata", "part.step", cancellation.Token))
            .ReturnsAsync((OrderFileResponse?)null);
        var controller = new FilesController(service.Object);

        var result = await controller.CreateOrderFileEntryAsync(id, "metadata", "part.step", cancellation.Token);

        var missing = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, missing.StatusCode);
        Assert.Equal("Order not found", Assert.IsType<string>(missing.Value));
        service.Verify(value => value.CreateFileAsync(id, "metadata", "part.step", cancellation.Token), Times.Once);
        service.VerifyNoOtherCalls();
    }
}
