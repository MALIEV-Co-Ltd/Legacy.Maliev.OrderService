using System.Text.Json;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    private const string BeforeCatalogNormalizer = "20261024180000_NormalizeOrderTextSourceStorage";
    private const string WithCatalogNormalizer = "20261024190000_NormalizeCatalogTextSourceStorage";
    private static readonly (string Table, string Field)[] CatalogColumns = [("Category", "Name"), ("FileFormat", "Name"), ("FileFormat", "Extension")];

    public static IEnumerable<object[]> CatalogColumnPreimages
    {
        get
        {
            foreach (var (table, columnName) in CatalogColumns)
                foreach (var drift in new[] { "type", "default", "nullability", "generated", "check" })
                    foreach (var downgrade in new[] { false, true })
                        yield return [table, columnName, drift, downgrade];
        }
    }

    public static IEnumerable<object[]> CatalogObjectCollisions
    {
        get
        {
            foreach (var table in new[] { "Category", "FileFormat" })
                foreach (var drift in new[] { "function", "overload", "named-trigger", "foreign-trigger" })
                    yield return [table, drift];
        }
    }

    public static IEnumerable<object[]> CatalogDowngradeCustody
    {
        get
        {
            foreach (var table in new[] { "Category", "FileFormat" })
                foreach (var drift in new[] { "disabled", "missing", "columns", "kind", "function", "overload", "foreign-dependent", "search-path", "function-owner", "definer", "table-owner" })
                    yield return [table, drift];
        }
    }

    [Fact]
    public async Task CatalogNormalizerModelRetainsNullableTextMetadataAndEveryExistingCheck()
    {
        await RunCatalogMigrationAsync(async (_, context, token) =>
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                foreach (var (table, field) in CatalogColumns)
                {
                    var entity = model.FindEntityType(table == "Category" ? typeof(Category).FullName! : typeof(FileFormat).FullName!);
                    Assert.NotNull(entity);
                    var property = entity.FindProperty(field);
                    Assert.NotNull(property);
                    Assert.True(property.IsNullable);
                    Assert.Equal(50, property.GetMaxLength());
                    Assert.Equal("text", property.GetColumnType());
                    Assert.Null(property.GetDefaultValue());
                    Assert.Null(property.GetDefaultValueSql());
                    Assert.Contains(entity.GetCheckConstraints(), check => check.Name == $"CK_{table}_{field}_SourceLength");
                }
            }
            Assert.Contains(WithCatalogNormalizer, await context.Database.GetAppliedMigrationsAsync(token));
        });
    }

    [Fact]
    public async Task CatalogNormalizerUpDownUpPreservesEveryRowVersionAndUnrelatedSchemaIncludingCollations()
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            await MigrationExecuteAsync(context, """
                ALTER TABLE public."Category" ALTER COLUMN "Name" TYPE text COLLATE pg_catalog."C";
                ALTER TABLE public."FileFormat" ALTER COLUMN "Name" TYPE text COLLATE pg_catalog."POSIX",
                    ALTER COLUMN "Extension" TYPE text COLLATE pg_catalog."C";
                """, token);
            var before = await CatalogMigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            await MigrateNormalizerAsync(context, WithCatalogNormalizer, token);
            var upgraded = await CatalogMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.NotEqual(before.OwnedObjects, upgraded.OwnedObjects);
            await MigrateNormalizerAsync(context, WithCatalogNormalizer, token);
            Assert.Equal(upgraded, await CatalogMigrationSnapshotAsync(app, context, token));
            await MigrateNormalizerAsync(context, BeforeCatalogNormalizer, token);
            Assert.Equal(before, await CatalogMigrationSnapshotAsync(app, context, token));
            await MigrateNormalizerAsync(context, WithCatalogNormalizer, token);
            Assert.Equal(upgraded, await CatalogMigrationSnapshotAsync(app, context, token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeCatalogNormalizer);
    }

    [Theory]
    [MemberData(nameof(CatalogColumnPreimages))]
    public async Task CatalogNormalizerRefusesColumnDriftInEitherDirectionWithoutChangingRowsOrSchema(string table, string field, string drift, bool downgrade)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            var check = $"CK_{table}_{field}_SourceLength";
            var function = CatalogFunction(table);
            var columns = table == "Category" ? "\"Name\"" : "\"Name\",\"Extension\"";
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"DROP TRIGGER \"{function}\" ON public.\"{table}\"", token);
            await MigrationExecuteAsync(context, drift switch
            {
                "type" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" TYPE character varying(50)",
                "default" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" SET DEFAULT 'unexpected'",
                "nullability" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" SET NOT NULL",
                "generated" => $$"""
                    ALTER TABLE public."{{table}}" DROP CONSTRAINT "{{check}}";
                    ALTER TABLE public."{{table}}" DROP COLUMN "{{field}}";
                    ALTER TABLE public."{{table}}" ADD COLUMN "{{field}}" text GENERATED ALWAYS AS ('retained'::text) STORED;
                    ALTER TABLE public."{{table}}" ADD CONSTRAINT "{{check}}" CHECK
                        (char_length("{{field}}") + char_length(regexp_replace("{{field}}" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50);
                    """,
                "check" => $"ALTER TABLE public.\"{table}\" DROP CONSTRAINT \"{check}\"; ALTER TABLE public.\"{table}\" ADD CONSTRAINT \"{check}\" CHECK (true)",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"CREATE TRIGGER \"{function}\" BEFORE INSERT OR UPDATE OF {columns} ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()", token);
            await AssertCatalogNormalizerRefusedAsync(app, context, downgrade ? BeforeCatalogNormalizer : WithCatalogNormalizer, token);
        }, downgrade ? WithCatalogNormalizer : BeforeCatalogNormalizer);
    }

    [Theory]
    [MemberData(nameof(CatalogObjectCollisions))]
    public async Task CatalogNormalizerUpgradeRefusesEveryNamedOverloadAndForeignTriggerCollision(string table, string drift)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            var function = CatalogFunction(table);
            await MigrationExecuteAsync(context, drift switch
            {
                "function" => $"CREATE FUNCTION public.\"{function}\"() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                "overload" => $"CREATE FUNCTION public.\"{function}\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "named-trigger" or "foreign-trigger" => $$"""
                    CREATE FUNCTION public.catalog_collision() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END;';
                    CREATE TRIGGER "{{(drift == "named-trigger" ? function : "foreign_catalog")}}" BEFORE INSERT ON public."{{table}}"
                        FOR EACH ROW EXECUTE FUNCTION public.catalog_collision();
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertCatalogNormalizerRefusedAsync(app, context, WithCatalogNormalizer, token);
        }, BeforeCatalogNormalizer);
    }

    [Theory]
    [MemberData(nameof(CatalogDowngradeCustody))]
    public async Task CatalogNormalizerDowngradeRefusesChangedOrSharedCustodyWithoutRemovingAnyObject(string table, string drift)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            var function = CatalogFunction(table);
            var columns = table == "Category" ? "\"Name\"" : "\"Name\",\"Extension\"";
            await MigrationExecuteAsync(context, drift switch
            {
                "disabled" => $"ALTER TABLE public.\"{table}\" DISABLE TRIGGER \"{function}\"",
                "missing" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"",
                "columns" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"; CREATE TRIGGER \"{function}\" BEFORE INSERT OR UPDATE OF \"CreatedDate\" ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "kind" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"; CREATE TRIGGER \"{function}\" BEFORE UPDATE OF {columns} ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "function" => $"CREATE OR REPLACE FUNCTION public.\"{function}\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "overload" => $"CREATE FUNCTION public.\"{function}\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "foreign-dependent" => $"CREATE TRIGGER foreign_catalog_use BEFORE UPDATE OF \"Name\" ON public.\"Order\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "search-path" => $"ALTER FUNCTION public.\"{function}\"() SET search_path = public,pg_catalog",
                "function-owner" => $"ALTER FUNCTION public.\"{function}\"() OWNER TO pg_database_owner",
                "definer" => $"ALTER FUNCTION public.\"{function}\"() SECURITY DEFINER",
                "table-owner" => $"ALTER TABLE public.\"{table}\" OWNER TO pg_database_owner",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertCatalogNormalizerRefusedAsync(app, context, BeforeCatalogNormalizer, token);
        });
    }

    [Theory]
    [InlineData("Category", "row-security")]
    [InlineData("FileFormat", "row-security")]
    [InlineData("Category", "rule")]
    [InlineData("FileFormat", "rule")]
    [InlineData("Category", "inheritance")]
    [InlineData("FileFormat", "inheritance")]
    public async Task CatalogNormalizerRefusesForeignTableSemanticsBeforeInstallingAnything(string table, string drift)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "row-security" => $"ALTER TABLE public.\"{table}\" ENABLE ROW LEVEL SECURITY",
                "rule" => $"CREATE RULE foreign_catalog_rule AS ON INSERT TO public.\"{table}\" DO INSTEAD NOTHING",
                "inheritance" => $"CREATE TABLE public.catalog_child () INHERITS (public.\"{table}\")",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertCatalogNormalizerRefusedAsync(app, context, WithCatalogNormalizer, token);
        }, BeforeCatalogNormalizer);
    }

    [Theory]
    [InlineData("Category")]
    [InlineData("FileFormat")]
    public async Task CatalogNormalizerRowAdmissionRefuses10001AndAccepts10000InBothDirections(string table)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            await MigrationExecuteAsync(context, $"INSERT INTO public.\"{table}\" (\"ID\") SELECT value FROM generate_series(3,10001) value", token);
            await AssertCatalogNormalizerRefusedAsync(app, context, WithCatalogNormalizer, token);
            await MigrationExecuteAsync(context, $"DELETE FROM public.\"{table}\" WHERE \"ID\"=10001", token);
            var before = await CatalogMigrationSnapshotAsync(app, context, token);
            await MigrateNormalizerAsync(context, WithCatalogNormalizer, token);
            var upgraded = await CatalogMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            await MigrationExecuteAsync(context, $"INSERT INTO public.\"{table}\" (\"ID\") VALUES (10001)", token);
            await AssertCatalogNormalizerRefusedAsync(app, context, BeforeCatalogNormalizer, token);
            await MigrationExecuteAsync(context, $"DELETE FROM public.\"{table}\" WHERE \"ID\"=10001", token);
            var afterDelete = await CatalogMigrationSnapshotAsync(app, context, token);
            await MigrateNormalizerAsync(context, BeforeCatalogNormalizer, token);
            var downgraded = await CatalogMigrationSnapshotAsync(app, context, token);
            Assert.Equal(afterDelete.Rows, downgraded.Rows);
            Assert.Equal(afterDelete.ExistingSchema, downgraded.ExistingSchema);
        }, BeforeCatalogNormalizer);
    }

    [Fact]
    public async Task CatalogNormalizerPreservesAnExistingForeignTemporaryGuardTable()
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            await MigrationExecuteAsync(context, """
                CREATE TEMP TABLE "__CatalogTextPaddingGuard" ("Marker" text);
                INSERT INTO "__CatalogTextPaddingGuard" VALUES ('retained foreign guard');
                """, token);
            var before = await CatalogMigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateNormalizerAsync(context, WithCatalogNormalizer, token));
            Assert.Equal("P0001", error.SqlState);
            Assert.Equal(before, await CatalogMigrationSnapshotAsync(app, context, token));
            Assert.Equal("retained foreign guard", await MigrationScalarAsync(context, "SELECT \"Marker\" FROM pg_temp.\"__CatalogTextPaddingGuard\"", token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeCatalogNormalizer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogFileFormatPairedFieldsNormalizeTogetherAndOtherOverflowRefusesAtomically(bool update)
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            var id = update ? 1 : 20;
            var before = await CatalogSnapshotAsync(app, token, "FileFormat", id);
            var dates = update ? await MigrationScalarAsync(context, "SELECT (to_jsonb(t)-'Name'-'Extension')::text FROM public.\"FileFormat\" t WHERE \"ID\"=1", token) : null;
            await MigrationExecuteAsync(context, update
                ? "UPDATE public.\"FileFormat\" SET \"Name\"=repeat('A',50)||' ',\"Extension\"=repeat('ก',50)||'  ' WHERE \"ID\"=1"
                : "INSERT INTO public.\"FileFormat\" (\"ID\",\"Name\",\"Extension\") VALUES (20,repeat('A',50)||' ',repeat('ก',50)||'  ')", token);
            Assert.Equal(new string('A', 50), await CatalogValueAsync(context, "FileFormat", "Name", id, token));
            Assert.Equal(new string('ก', 50), await CatalogValueAsync(context, "FileFormat", "Extension", id, token));
            if (update) Assert.Equal(dates, await MigrationScalarAsync(context, "SELECT (to_jsonb(t)-'Name'-'Extension')::text FROM public.\"FileFormat\" t WHERE \"ID\"=1", token));
            Assert.Equal(before, await CatalogSnapshotAsync(app, token, "FileFormat", id));
            var after = await CatalogMigrationSnapshotAsync(app, context, token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationExecuteAsync(context,
                $"UPDATE public.\"FileFormat\" SET \"Name\"=repeat('A',50)||' ',\"Extension\"=repeat('ก',50)||chr(160) WHERE \"ID\"={id}", token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("CK_FileFormat_Extension_SourceLength", error.ConstraintName);
            Assert.Equal(after, await CatalogMigrationSnapshotAsync(app, context, token));
        });
    }

    [Fact]
    public async Task CatalogNormalizerNeverDropsAPublicGuardNameCollisionWhenTemporarySchemaIsLast()
    {
        await RunCatalogMigrationAsync(async (app, context, token) =>
        {
            await SeedCatalogPaddingAsync(app, token);
            var originalPath = await MigrationScalarAsync(context, "SHOW search_path", token);
            Assert.NotNull(originalPath);
            try
            {
                await MigrationExecuteAsync(context, """
                    CREATE TABLE public."__CatalogTextPaddingGuard" ("Marker" text NOT NULL);
                    INSERT INTO public."__CatalogTextPaddingGuard" VALUES ('retained public sentinel');
                    SET search_path = public,pg_temp;
                    """, token);
                var explicitPath = await MigrationScalarAsync(context, "SHOW search_path", token);
                const string sentinelSql = """
                    SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."Marker")::text
                    FROM public."__CatalogTextPaddingGuard" t
                    """;
                var sentinel = await MigrationScalarAsync(context, sentinelSql, token);
                Assert.NotNull(sentinel);
                const string identitySql = """SELECT to_regclass('public."__CatalogTextPaddingGuard"')::oid::text""";
                var publicOid = await MigrationScalarAsync(context, identitySql, token);
                Assert.NotNull(publicOid);
                var before = await CatalogMigrationSnapshotAsync(app, context, token);
                await MigrateNormalizerAsync(context, WithCatalogNormalizer, token);
                var upgraded = await CatalogMigrationSnapshotAsync(app, context, token);
                Assert.Equal(before.Rows, upgraded.Rows);
                Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
                Assert.Equal(sentinel, await MigrationScalarAsync(context, sentinelSql, token));
                Assert.Equal(publicOid, await MigrationScalarAsync(context, identitySql, token));
                Assert.Equal(explicitPath, await MigrationScalarAsync(context, "SHOW search_path", token));
                await MigrateNormalizerAsync(context, BeforeCatalogNormalizer, token);
                Assert.Equal(before, await CatalogMigrationSnapshotAsync(app, context, token));
                Assert.Equal(sentinel, await MigrationScalarAsync(context, sentinelSql, token));
                Assert.Equal(publicOid, await MigrationScalarAsync(context, identitySql, token));
                Assert.Equal(explicitPath, await MigrationScalarAsync(context, "SHOW search_path", token));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "SELECT set_config('search_path',@path,false)";
                command.Parameters.Add(new NpgsqlParameter("path", NpgsqlDbType.Text) { Value = originalPath });
                await command.ExecuteNonQueryAsync(cleanup.Token);
            }
            Assert.Equal(originalPath, await MigrationScalarAsync(context, "SHOW search_path", token));
        }, BeforeCatalogNormalizer);
    }

    private Task RunCatalogMigrationAsync(Func<WebApplicationFactory<Program>, OrderDbContext, CancellationToken, Task> action, string target = WithCatalogNormalizer) =>
        RunMigrationAsync(action, target);

    private static string CatalogFunction(string table) => table switch
    {
        "Category" => "NormalizeCategoryTextSource",
        "FileFormat" => "NormalizeFileFormatTextSource",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static async Task AssertCatalogNormalizerRefusedAsync(WebApplicationFactory<Program> app, OrderDbContext context, string target, CancellationToken token)
    {
        var before = await CatalogMigrationSnapshotAsync(app, context, token);
        var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
        var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateNormalizerAsync(context, target, token));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await CatalogMigrationSnapshotAsync(app, context, token));
        Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal("True", await MigrationScalarAsync(context, "SELECT to_regclass('pg_temp.\"__CatalogTextPaddingGuard\"') IS NULL", token));
    }

    private sealed record CatalogMigrationSnapshot(string Rows, string? ExistingSchema, string? OwnedObjects, string History);

    private static async Task<CatalogMigrationSnapshot> CatalogMigrationSnapshotAsync(WebApplicationFactory<Program> app, OrderDbContext context, CancellationToken token) =>
        new(await CatalogSnapshotAsync(app, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'tables',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relowner,c.relrowsecurity,c.relforcerowsecurity,c.relkind,c.relpersistence) ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN ('r','p')),
                    'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated,c.conislocal,c.coninhcount,c.connoinherit,c.conkey) ORDER BY c.conrelid,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                    'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal AND t.tgname NOT IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource')),
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname NOT IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource')),
                    'rules',(SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY r.oid) FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class WHERE c.relnamespace='public'::regnamespace),
                    'inheritance',(SELECT jsonb_agg(row_to_json(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid WHERE c.relnamespace='public'::regnamespace))::text
                """, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource')),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t WHERE NOT t.tgisinternal AND (t.tgname IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource') OR t.tgfoid IN (SELECT p.oid FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource')))))::text
                """, token),
            JsonSerializer.Serialize(await context.Database.GetAppliedMigrationsAsync(token)));
}
