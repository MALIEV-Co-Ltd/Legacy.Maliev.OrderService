using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Data;

internal static class OrderTextSourceConstraints
{
    internal const string NameLengthSql = """
        char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100
        """;
    internal const string DescriptionLengthSql = """
        char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250
        """;

    internal static void Validate(UpsertOrderRequest request)
    {
        if (request.Name is { Length: > 100 } || request.Description is { Length: > 250 })
            throw new ArgumentException("The request is invalid.", nameof(request));
    }
}
