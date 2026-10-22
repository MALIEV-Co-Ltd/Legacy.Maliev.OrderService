using Legacy.Maliev.OrderService.Application.Models;

namespace Legacy.Maliev.OrderService.Data;

internal static class NullableCatalogSourceConstraints
{
    internal const string NameLengthSql = ProcessSourceConstraints.NameLengthSql;
    internal const string ExtensionLengthSql = """
        char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50
        """;

    internal static void ValidateCategory(UpsertCategoryRequest request)
    {
        if (request.Name is { Length: > 50 })
            throw new ArgumentException("The request is invalid.", nameof(request));
    }

    internal static void ValidateFileFormat(UpsertFileFormatRequest request)
    {
        if (request.Name is { Length: > 50 } || request.Extension is { Length: > 50 })
            throw new ArgumentException("The request is invalid.", nameof(request));
    }
}
