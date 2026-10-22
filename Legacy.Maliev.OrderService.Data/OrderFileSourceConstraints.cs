namespace Legacy.Maliev.OrderService.Data;

internal static class OrderFileSourceConstraints
{
    internal const string BucketLengthSql = """
        char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50
        """;

    internal static void ValidateBucket(string bucket)
    {
        if (bucket is null || bucket.Length > 50)
            throw new ArgumentException("The request is invalid.", nameof(bucket));
    }
}
