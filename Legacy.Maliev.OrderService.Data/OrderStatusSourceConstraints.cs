using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Data;

internal static class OrderStatusSourceConstraints
{
    // SQL Server nvarchar counts UTF-16 units; PostgreSQL character counts do not.
    internal const string NameLengthSql = """
        char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50
        """;
    internal const string DescriptionLengthSql = """
        char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100
        """;

    internal static void Validate(UpsertOrderStatusRequest request)
    {
        if (request.Name is null || request.Name.Length > 50 || request.Description?.Length > 100)
        {
            throw new ArgumentException("The request is invalid.", nameof(request));
        }
    }
}
