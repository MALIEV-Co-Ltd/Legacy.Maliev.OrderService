using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data;

public sealed partial class OrderRepository
{
    /// <summary>Reads the original filtered category snapshot without changing internal process responses.</summary>
    /// <param name="category">The exact category name, or null for an unfiltered read.</param>
    /// <param name="c">Cancellation for the database read.</param>
    /// <returns>Process rows with category scalars only when the source query loaded them.</returns>
    public async Task<IReadOnlyList<ProcessReadResult>> GetProcessReadsAsync(string? category, CancellationToken c)
    {
        if (category is null)
        {
            var rows = await GetProcessesAsync(null, c);
            return rows.Select(row => new ProcessReadResult(row, null)).ToArray();
        }

        return await orders.Processes.AsNoTracking()
            .Where(row => row.Category != null && row.Category.Name == category)
            .OrderBy(row => row.Id)
            .Select(row => new ProcessReadResult(
                new ProcessResponse(row.Id, row.CategoryId, row.Name, row.CreatedDate, row.ModifiedDate),
                new CategoryResponse(row.Category!.Id, row.Category.Name, row.Category.CreatedDate, row.Category.ModifiedDate)))
            .ToListAsync(c);
    }
}
