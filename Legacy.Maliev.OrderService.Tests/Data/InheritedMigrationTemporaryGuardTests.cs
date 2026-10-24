using System.Data;
using System.Text.Json;
using Legacy.Maliev.OrderService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

// Legacy safety correction under Order44, not an original-source SHA disposition.
// Exact accepted source and per-file introduction/parent/blob bindings are retained in the bounded bundle.
public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    private static readonly (string Table, string Key)[] GuardOrderTables =
        [("Order", "ID"), ("Category", "ID"), ("Process", "ID"), ("FileFormat", "ID"), ("OrderFile", "ID"), ("OrderDeletionIntent", "OrderId")];
    private static readonly (string Table, string Key)[] GuardStatusTables =
        [("OrderStatus", "ID"), ("OrderStatusHasPossibleStatus", "ID"), ("OrderStatusHistory", "ID")];

    [Theory]
    [InlineData("process-down")]
    [InlineData("catalog-down")]
    [InlineData("bucket-down")]
    [InlineData("order-text-up")]
    [InlineData("order-text-down")]
    [InlineData("tracking-down")]
    [InlineData("padding-up")]
    [InlineData("padding-down")]
    [InlineData("status-down")]
    public async Task InheritedMigrationCleanupPreservesPublicGuardIdentitySchemaRowsAndXmin(string name)
    {
        var test = GuardCase(name);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await using var scope = app.Services.CreateAsyncScope();
        var order = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var status = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        DbContext changing = test.StatusDatabase ? status : order;
        DbContext unchanged = test.StatusDatabase ? order : status;
        order.Database.SetCommandTimeout(30);
        status.Database.SetCommandTimeout(30);
        await order.Database.OpenConnectionAsync(token);
        await status.Database.OpenConnectionAsync(token);
        string? originalPath = null;
        try
        {
            // Setup runs before introducing the public collision or nondefault lookup order.
            await changing.GetService<IMigrator>().MigrateAsync(test.From, token);
            await SeedMigrationAsync(app, order, token);
            originalPath = await MigrationScalarAsync(changing, "SHOW search_path", token);
            Assert.NotNull(originalPath);
            await MigrationExecuteAsync(changing, $"""
                CREATE TABLE public."{test.Marker}" ("ID" integer PRIMARY KEY, "Marker" text NOT NULL);
                INSERT INTO public."{test.Marker}" VALUES (1,'retained public sentinel');
                """, token);
            await SetGuardSearchPathAsync(changing, "public,pg_temp", token);
            var appliedPath = await MigrationScalarAsync(changing, "SHOW search_path", token);
            Assert.Equal("public,pg_temp", appliedPath?.Replace(" ", "", StringComparison.Ordinal));
            var relation = await GuardRelationAsync(changing, test.Marker, token);
            Assert.NotNull(relation);
            var marker = await GuardMarkerAsync(changing, test.Marker, token);
            var before = await GuardSnapshotAsync(order, status, test, token);
            var history = (await changing.Database.GetAppliedMigrationsAsync(token)).ToArray();
            var otherHistory = (await unchanged.Database.GetAppliedMigrationsAsync(token)).ToArray();
            var lockTimeout = await MigrationScalarAsync(changing, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(changing, "SHOW statement_timeout", token);

            await changing.GetService<IMigrator>().MigrateAsync(test.To, token);

            // A missing relation is an assertion failure, not a later missing-table SQL error.
            Assert.Equal(relation, await GuardRelationAsync(changing, test.Marker, token));
            Assert.Equal(marker, await GuardMarkerAsync(changing, test.Marker, token));
            Assert.Equal(before, await GuardSnapshotAsync(order, status, test, token));
            var expected = string.CompareOrdinal(test.From, test.To) < 0
                ? history.Append(test.To).ToArray()
                : history.Where(value => value != test.From).ToArray();
            Assert.Equal(expected, (await changing.Database.GetAppliedMigrationsAsync(token)).ToArray());
            Assert.Equal(otherHistory, (await unchanged.Database.GetAppliedMigrationsAsync(token)).ToArray());
            Assert.Equal("True", await MigrationScalarAsync(changing, $"SELECT to_regclass('pg_temp.\"{test.Marker}\"') IS NULL", token));
            Assert.Equal(appliedPath, await MigrationScalarAsync(changing, "SHOW search_path", token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(changing, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(changing, "SHOW statement_timeout", token));
            Assert.Null(order.Database.CurrentTransaction);
            Assert.Null(status.Database.CurrentTransaction);
        }
        finally
        {
            try
            {
                if (originalPath is not null && changing.Database.GetDbConnection().State == ConnectionState.Open)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await SetGuardSearchPathAsync(changing, originalPath, cleanup.Token);
                    Assert.Equal(originalPath, await MigrationScalarAsync(changing, "SHOW search_path", cleanup.Token));
                }
            }
            finally
            {
                try { await order.Database.CloseConnectionAsync(); }
                finally { await status.Database.CloseConnectionAsync(); }
            }
        }
    }

    private sealed record MigrationGuardCase(bool StatusDatabase, string From, string To, string Marker,
        string[] ChangedTables, string[] ChangedFunctions);

    private static MigrationGuardCase GuardCase(string name) => name switch
    {
        "process-down" => new(false, "20261007140000_RequireProcessSourceName", "20261001051000_AddOrderDeletionIntent",
            "__ProcessSourceNameGuard", ["Process"], []),
        "catalog-down" => new(false, "20261018100000_EnforceNullableCatalogSourceStrings", "20261007140000_RequireProcessSourceName",
            "__NullableCatalogSourceStringsGuard", ["Category", "FileFormat"], []),
        "bucket-down" => new(false, "20261018110000_EnforceOrderFileBucketSourceLength", "20261018100000_EnforceNullableCatalogSourceStrings",
            "__OrderFileBucketSourceGuard", ["OrderFile"], []),
        "order-text-up" => new(false, "20261018110000_EnforceOrderFileBucketSourceLength", "20261018120000_EnforceNullableOrderTextSourceLengths",
            "__OrderTextSourceGuard", ["Order"], []),
        "order-text-down" => new(false, "20261018120000_EnforceNullableOrderTextSourceLengths", "20261018110000_EnforceOrderFileBucketSourceLength",
            "__OrderTextSourceGuard", ["Order"], []),
        "tracking-down" => new(false, "20261024130000_EnforceTrackingSourceStorage", "20261018120000_EnforceNullableOrderTextSourceLengths",
            "__OrderTrackingSourceGuard", ["Order"], ["NormalizeOrderTrackingSource"]),
        "padding-up" => new(false, "20261024130000_EnforceTrackingSourceStorage", "20261024180000_NormalizeOrderTextSourceStorage",
            "__OrderTextPaddingGuard", ["Order"], ["NormalizeOrderTextSource"]),
        "padding-down" => new(false, "20261024180000_NormalizeOrderTextSourceStorage", "20261024130000_EnforceTrackingSourceStorage",
            "__OrderTextPaddingGuard", ["Order"], ["NormalizeOrderTextSource"]),
        "status-down" => new(true, "20261007120000_RequireOrderStatusSourceStrings", "20260721030107_FixTimestampColumnType",
            "__OrderStatusSourceStringGuard", ["OrderStatus"], []),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static async Task SetGuardSearchPathAsync(DbContext context, string value, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT pg_catalog.set_config('search_path',@value,false)";
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value });
        await command.ExecuteScalarAsync(token);
    }

    private static Task<string?> GuardRelationAsync(DbContext context, string marker, CancellationToken token) =>
        MigrationScalarAsync(context, $"SELECT to_regclass('public.\"{marker}\"')::oid::text", token);

    private static Task<string?> GuardMarkerAsync(DbContext context, string marker, CancellationToken token) =>
        MigrationScalarAsync(context, $"SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t.\"ID\")::text FROM public.\"{marker}\" t", token);

    private sealed record TemporaryGuardSnapshot(string Rows, string? OrderSchema, string? StatusSchema);

    private static async Task<TemporaryGuardSnapshot> GuardSnapshotAsync(OrderDbContext order, OrderStatusDbContext status,
        MigrationGuardCase test, CancellationToken token)
    {
        var rows = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        var castsTarget = test.Marker != "__OrderTextPaddingGuard";
        string[] changedTables = castsTarget ? test.ChangedTables : [];
        foreach (var (table, key) in GuardOrderTables)
            rows.Add("Order." + table, await GuardRowsAsync(order, table, key, castsTarget && !test.StatusDatabase && test.ChangedTables.Contains(table), token));
        foreach (var (table, key) in GuardStatusTables)
            rows.Add("Status." + table, await GuardRowsAsync(status, table, key, castsTarget && test.StatusDatabase && test.ChangedTables.Contains(table), token));
        return new(JsonSerializer.Serialize(rows),
            await GuardUnrelatedSchemaAsync(order, test.StatusDatabase ? [] : changedTables, test.StatusDatabase ? [] : test.ChangedFunctions, token),
            await GuardUnrelatedSchemaAsync(status, test.StatusDatabase ? changedTables : [], test.StatusDatabase ? test.ChangedFunctions : [], token));
    }

    private static Task<string?> GuardRowsAsync(DbContext context, string table, string key, bool permitsTargetRewrite, CancellationToken token)
    {
        var version = permitsTargetRewrite ? "" : "||jsonb_build_object('__xmin',t.xmin::text)";
        return MigrationScalarAsync(context, $"SELECT COALESCE(jsonb_agg(to_jsonb(t){version} ORDER BY t.\"{key}\"),'[]'::jsonb)::text FROM public.\"{table}\" t", token);
    }

    private static async Task<string?> GuardUnrelatedSchemaAsync(DbContext context, string[] changedTables,
        string[] changedFunctions, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            SELECT jsonb_build_object(
                'tables',(SELECT jsonb_agg(jsonb_build_array(c.oid,c.relname,c.relkind,c.relowner,c.relrowsecurity,c.relforcerowsecurity,c.relpersistence) ORDER BY c.relname)
                    FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN ('r','p')),
                'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position)
                    FROM information_schema.columns c WHERE c.table_schema='public' AND NOT c.table_name=ANY(@tables)),
                'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated,c.conislocal,c.coninhcount,c.connoinherit,c.conkey) ORDER BY c.conrelid,c.conname)
                    FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace AND NOT t.relname=ANY(@tables)),
                'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname)
                    FROM pg_indexes WHERE schemaname='public' AND NOT tablename=ANY(@tables)),
                'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname)
                    FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal AND NOT c.relname=ANY(@tables)
                    AND t.tgfoid NOT IN (SELECT p.oid FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname=ANY(@functions))),
                'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid))
                    FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND NOT p.proname=ANY(@functions)),
                'rules',(SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY r.oid)
                    FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class WHERE c.relnamespace='public'::regnamespace),
                'inheritance',(SELECT jsonb_agg(row_to_json(i) ORDER BY i.inhrelid,i.inhseqno)
                    FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid WHERE c.relnamespace='public'::regnamespace))::text
            """;
        command.Parameters.Add(new NpgsqlParameter("tables", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changedTables });
        command.Parameters.Add(new NpgsqlParameter("functions", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = changedFunctions });
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
