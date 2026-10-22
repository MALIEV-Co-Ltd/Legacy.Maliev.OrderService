using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Data;

internal static class ProcessSourceConstraints
{
    internal const string NameLengthSql = """
        char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50
        """;

    internal static void Validate(UpsertProcessRequest request)
    {
        if (request.Name is null || request.Name.Length > 50)
            throw new ArgumentException("The request is invalid.", nameof(request));
    }
}
