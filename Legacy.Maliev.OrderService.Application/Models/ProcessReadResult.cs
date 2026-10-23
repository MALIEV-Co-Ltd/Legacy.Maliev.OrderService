namespace Legacy.Maliev.OrderService.Application.Models;

/// <summary>A process and its optionally loaded category from one read snapshot.</summary>
/// <param name="Process">The unchanged internal process response.</param>
/// <param name="Category">The category loaded only by a filtered process read.</param>
public sealed record ProcessReadResult(ProcessResponse Process, CategoryResponse? Category);
