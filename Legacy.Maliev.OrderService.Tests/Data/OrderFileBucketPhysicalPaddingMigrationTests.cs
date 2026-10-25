using System.Text.Json;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    private const string BeforeBucketNormalizer = "20261024200000_NormalizeProcessTextSourceStorage";
    private const string WithBucketNormalizer = "20261025090000_NormalizeOrderFileBucketSourceStorage";
    private const string BucketNormalizerFunction = "NormalizeOrderFileBucketSource";

    public static IEnumerable<object[]> BucketColumnPreimages
    {
        get
        {
            foreach (var column in new[] { "Bucket", "ObjectName" })
                foreach (var drift in new[] { "type", "default", "nullability", "generated" })
                    foreach (var down in new[] { false, true })
                        yield return [column, drift, down];
            foreach (var down in new[] { false, true }) yield return ["Bucket", "check", down];
        }
    }

    [Fact]
    public async Task BucketNormalizerModelAndSnapshotRetainRequiredUnboundedCompanionAndExistingCheck()
    {
        await RunBucketMigrationAsync(async (_, context, token) =>
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                var entity = model.FindEntityType(typeof(OrderFile).FullName!);
                Assert.NotNull(entity);
                foreach (var field in new[] { "Bucket", "ObjectName" })
                {
                    var property = entity.FindProperty(field);
                    Assert.NotNull(property);
                    Assert.False(property.IsNullable);
                    Assert.Equal(field == "Bucket" ? 50 : (int?)null, property.GetMaxLength());
                    Assert.Equal("text", property.GetColumnType());
                    Assert.Null(property.GetDefaultValue());
                    Assert.Null(property.GetDefaultValueSql());
                }
                Assert.Contains(entity.GetCheckConstraints(), check => check.Name == "CK_OrderFile_Bucket_SourceLength");
            }
            Assert.Contains(WithBucketNormalizer, await context.Database.GetAppliedMigrationsAsync(token));
        });
    }

    [Fact]
    public async Task BucketNormalizerUpDownUpPreservesNineTablesVersionsOtherDatabaseAndCollations()
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, """
                ALTER TABLE public."OrderFile" ALTER COLUMN "Bucket" TYPE text COLLATE pg_catalog."C",
                    ALTER COLUMN "ObjectName" TYPE text COLLATE pg_catalog."POSIX";
                """, token);
            var before = await BucketMigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            await MigrateBucketNormalizerAsync(context, WithBucketNormalizer, token);
            var upgraded = await BucketMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
            Assert.NotEqual(before.OwnedObjects, upgraded.OwnedObjects);
            await MigrateBucketNormalizerAsync(context, WithBucketNormalizer, token);
            Assert.Equal(upgraded, await BucketMigrationSnapshotAsync(app, context, token));
            await MigrateBucketNormalizerAsync(context, BeforeBucketNormalizer, token);
            Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
            await MigrateBucketNormalizerAsync(context, WithBucketNormalizer, token);
            Assert.Equal(upgraded, await BucketMigrationSnapshotAsync(app, context, token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeBucketNormalizer);
    }

    [Theory]
    [MemberData(nameof(BucketColumnPreimages))]
    public async Task BucketNormalizerRefusesChangedBucketOrCompanionColumnWithoutPartialDdl(string field, string drift, bool downgrade)
    {
        Assert.Contains(field, new[] { "Bucket", "ObjectName" });
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"DROP TRIGGER \"{BucketNormalizerFunction}\" ON public.\"OrderFile\"", token);
            var check = field == "Bucket" ? "ALTER TABLE public.\"OrderFile\" DROP CONSTRAINT \"CK_OrderFile_Bucket_SourceLength\";" : "";
            var recreate = field == "Bucket" ? """
                ALTER TABLE public."OrderFile" ADD CONSTRAINT "CK_OrderFile_Bucket_SourceLength" CHECK
                    (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50);
                """ : "";
            await MigrationExecuteAsync(context, drift switch
            {
                "type" => $"ALTER TABLE public.\"OrderFile\" ALTER COLUMN \"{field}\" TYPE character varying({(field == "Bucket" ? 50 : 7000)})",
                "default" => $"ALTER TABLE public.\"OrderFile\" ALTER COLUMN \"{field}\" SET DEFAULT 'unexpected'",
                "nullability" => $"ALTER TABLE public.\"OrderFile\" ALTER COLUMN \"{field}\" DROP NOT NULL",
                "generated" => check + $"ALTER TABLE public.\"OrderFile\" DROP COLUMN \"{field}\"; ALTER TABLE public.\"OrderFile\" ADD COLUMN \"{field}\" text GENERATED ALWAYS AS ('retained'::text) STORED NOT NULL;" + recreate,
                "check" => "ALTER TABLE public.\"OrderFile\" DROP CONSTRAINT \"CK_OrderFile_Bucket_SourceLength\"; ALTER TABLE public.\"OrderFile\" ADD CONSTRAINT \"CK_OrderFile_Bucket_SourceLength\" CHECK (true)",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"CREATE TRIGGER \"{BucketNormalizerFunction}\" BEFORE INSERT OR UPDATE OF \"Bucket\" ON public.\"OrderFile\" FOR EACH ROW EXECUTE FUNCTION public.\"{BucketNormalizerFunction}\"()", token);
            await AssertBucketNormalizerRefusedAsync(app, context, downgrade ? BeforeBucketNormalizer : WithBucketNormalizer, token);
        }, downgrade ? WithBucketNormalizer : BeforeBucketNormalizer);
    }

    [Theory]
    [InlineData("function")]
    [InlineData("overload")]
    [InlineData("named-trigger")]
    [InlineData("foreign-trigger")]
    public async Task BucketNormalizerUpgradeRefusesNamedOverloadsAndForeignTriggers(string drift)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "function" => $"CREATE FUNCTION public.\"{BucketNormalizerFunction}\"() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                "overload" => $"CREATE FUNCTION public.\"{BucketNormalizerFunction}\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "named-trigger" or "foreign-trigger" => $$"""
                    CREATE FUNCTION public.bucket_collision() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END;';
                    CREATE TRIGGER "{{(drift == "named-trigger" ? BucketNormalizerFunction : "foreign_bucket")}}" BEFORE INSERT ON public."OrderFile"
                        FOR EACH ROW EXECUTE FUNCTION public.bucket_collision();
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertBucketNormalizerRefusedAsync(app, context, WithBucketNormalizer, token);
        }, BeforeBucketNormalizer);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("columns")]
    [InlineData("kind")]
    [InlineData("function")]
    [InlineData("overload")]
    [InlineData("foreign-dependent")]
    [InlineData("search-path")]
    [InlineData("function-owner")]
    [InlineData("definer")]
    [InlineData("table-owner")]
    [InlineData("strict")]
    [InlineData("parallel")]
    [InlineData("volatility")]
    [InlineData("leakproof")]
    public async Task BucketNormalizerDowngradeRefusesChangedOrSharedCustody(string drift)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "disabled" => $"ALTER TABLE public.\"OrderFile\" DISABLE TRIGGER \"{BucketNormalizerFunction}\"",
                "missing" => $"DROP TRIGGER \"{BucketNormalizerFunction}\" ON public.\"OrderFile\"",
                "columns" => $"DROP TRIGGER \"{BucketNormalizerFunction}\" ON public.\"OrderFile\"; CREATE TRIGGER \"{BucketNormalizerFunction}\" BEFORE INSERT OR UPDATE OF \"CreatedDate\" ON public.\"OrderFile\" FOR EACH ROW EXECUTE FUNCTION public.\"{BucketNormalizerFunction}\"()",
                "kind" => $"DROP TRIGGER \"{BucketNormalizerFunction}\" ON public.\"OrderFile\"; CREATE TRIGGER \"{BucketNormalizerFunction}\" BEFORE UPDATE OF \"Bucket\" ON public.\"OrderFile\" FOR EACH ROW EXECUTE FUNCTION public.\"{BucketNormalizerFunction}\"()",
                "function" => $"CREATE OR REPLACE FUNCTION public.\"{BucketNormalizerFunction}\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "overload" => $"CREATE FUNCTION public.\"{BucketNormalizerFunction}\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "foreign-dependent" => $"CREATE TABLE public.foreign_bucket_target (\"Bucket\" text); CREATE TRIGGER foreign_bucket_use BEFORE UPDATE OF \"Bucket\" ON public.foreign_bucket_target FOR EACH ROW EXECUTE FUNCTION public.\"{BucketNormalizerFunction}\"()",
                "search-path" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() SET search_path = public,pg_catalog",
                "function-owner" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() OWNER TO pg_database_owner",
                "definer" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() SECURITY DEFINER",
                "table-owner" => "ALTER TABLE public.\"OrderFile\" OWNER TO pg_database_owner",
                "strict" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() STRICT",
                "parallel" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() PARALLEL SAFE",
                "volatility" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() STABLE",
                "leakproof" => $"ALTER FUNCTION public.\"{BucketNormalizerFunction}\"() LEAKPROOF",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertBucketNormalizerRefusedAsync(app, context, BeforeBucketNormalizer, token);
        });
    }

    [Theory]
    [InlineData("row-security", false)]
    [InlineData("row-security", true)]
    [InlineData("rule", false)]
    [InlineData("rule", true)]
    [InlineData("inheritance", false)]
    [InlineData("inheritance", true)]
    public async Task BucketNormalizerRefusesForeignTableSemanticsInBothDirections(string drift, bool downgrade)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "row-security" => "ALTER TABLE public.\"OrderFile\" ENABLE ROW LEVEL SECURITY",
                "rule" => "CREATE RULE foreign_bucket_rule AS ON INSERT TO public.\"OrderFile\" DO INSTEAD NOTHING",
                "inheritance" => "CREATE TABLE public.bucket_child () INHERITS (public.\"OrderFile\")",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertBucketNormalizerRefusedAsync(app, context, downgrade ? BeforeBucketNormalizer : WithBucketNormalizer, token);
        }, downgrade ? WithBucketNormalizer : BeforeBucketNormalizer);
    }

    [Fact]
    public async Task BucketNormalizerRowAdmissionRefuses10001AndAccepts10000InBothDirections()
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, """INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") SELECT value,1,'Retained','row-control' FROM generate_series(3,10001) value""", token);
            await AssertBucketNormalizerRefusedAsync(app, context, WithBucketNormalizer, token);
            await MigrationExecuteAsync(context, """DELETE FROM public."OrderFile" WHERE "ID"=10001""", token);
            var before = await BucketMigrationSnapshotAsync(app, context, token);
            await MigrateBucketNormalizerAsync(context, WithBucketNormalizer, token);
            var upgraded = await BucketMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
            await MigrationExecuteAsync(context, """INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") VALUES (10001,1,'Retained','row-control')""", token);
            await AssertBucketNormalizerRefusedAsync(app, context, BeforeBucketNormalizer, token);
            await MigrationExecuteAsync(context, """DELETE FROM public."OrderFile" WHERE "ID"=10001""", token);
            var afterDelete = await BucketMigrationSnapshotAsync(app, context, token);
            await MigrateBucketNormalizerAsync(context, BeforeBucketNormalizer, token);
            var downgraded = await BucketMigrationSnapshotAsync(app, context, token);
            Assert.Equal(afterDelete.Rows, downgraded.Rows);
            Assert.Equal(afterDelete.ExistingSchema, downgraded.ExistingSchema);
            Assert.Equal(afterDelete.OtherDatabase, downgraded.OtherDatabase);
        }, BeforeBucketNormalizer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BucketNormalizerPreservesExistingForeignTemporaryGuard(bool downgrade)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            await MigrationExecuteAsync(context, """CREATE TEMP TABLE "__OrderFileBucketPaddingGuard" ("Marker" text); INSERT INTO "__OrderFileBucketPaddingGuard" VALUES ('retained foreign guard');""", token);
            var before = await BucketMigrationSnapshotAsync(app, context, token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateBucketNormalizerAsync(context, downgrade ? BeforeBucketNormalizer : WithBucketNormalizer, token));
            Assert.Equal("P0001", error.SqlState);
            Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
            Assert.Equal("retained foreign guard", await MigrationScalarAsync(context, "SELECT \"Marker\" FROM pg_temp.\"__OrderFileBucketPaddingGuard\"", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, downgrade ? WithBucketNormalizer : BeforeBucketNormalizer);
    }

    [Fact]
    public async Task BucketNormalizerNeverDropsPublicGuardSentinelWhenTemporarySchemaIsLast()
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            var original = await MigrationScalarAsync(context, "SHOW search_path", token);
            Assert.NotNull(original);
            try
            {
                await MigrationExecuteAsync(context, """CREATE TABLE public."__OrderFileBucketPaddingGuard" ("Marker" text NOT NULL); INSERT INTO public."__OrderFileBucketPaddingGuard" VALUES ('public sentinel'); SET search_path = public,pg_temp;""", token);
                var explicitPath = await MigrationScalarAsync(context, "SHOW search_path", token);
                Assert.NotNull(explicitPath);
                const string sentinel = """SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text))::text FROM public."__OrderFileBucketPaddingGuard" t""";
                const string identity = """SELECT to_regclass('public."__OrderFileBucketPaddingGuard"')::oid::text""";
                var rows = await MigrationScalarAsync(context, sentinel, token);
                var oid = await MigrationScalarAsync(context, identity, token);
                Assert.NotNull(rows);
                Assert.NotNull(oid);
                var before = await BucketMigrationSnapshotAsync(app, context, token);
                await MigrateBucketNormalizerAsync(context, WithBucketNormalizer, token);
                var upgraded = await BucketMigrationSnapshotAsync(app, context, token);
                Assert.Equal(before.Rows, upgraded.Rows);
                Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
                Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
                Assert.Equal(rows, await MigrationScalarAsync(context, sentinel, token));
                Assert.Equal(oid, await MigrationScalarAsync(context, identity, token));
                Assert.Equal(explicitPath, await MigrationScalarAsync(context, "SHOW search_path", token));
                await MigrateBucketNormalizerAsync(context, BeforeBucketNormalizer, token);
                Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
                Assert.Equal(rows, await MigrationScalarAsync(context, sentinel, token));
                Assert.Equal(oid, await MigrationScalarAsync(context, identity, token));
                Assert.Equal(explicitPath, await MigrationScalarAsync(context, "SHOW search_path", token));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "SELECT set_config('search_path',@path,false)";
                command.Parameters.Add(new NpgsqlParameter("path", NpgsqlDbType.Text) { Value = original });
                await command.ExecuteNonQueryAsync(cleanup.Token);
            }
            Assert.Equal(original, await MigrationScalarAsync(context, "SHOW search_path", token));
        }, BeforeBucketNormalizer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BucketNormalizerLockDeadlineRollsBackBothDirectionsWithoutChangingEitherDatabase(bool downgrade)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            var before = await BucketMigrationSnapshotAsync(app, context, token);
            await using var attempt = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options);
            Assert.False(attempt.Database.CreateExecutionStrategy().RetriesOnFailure);
            attempt.Database.SetCommandTimeout(40);
            await attempt.Database.OpenConnectionAsync(token);
            var lockTimeout = await MigrationScalarAsync(attempt, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(attempt, "SHOW statement_timeout", token);
            await using var blocker = new NpgsqlConnection(context.Database.GetConnectionString());
            await blocker.OpenAsync(token);
            await using var transaction = await blocker.BeginTransactionAsync(token);
            try
            {
                await using var command = new NpgsqlCommand("""LOCK TABLE public."OrderFile" IN ACCESS EXCLUSIVE MODE""", blocker, transaction) { CommandTimeout = 40 };
                await command.ExecuteNonQueryAsync(token);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateBucketNormalizerAsync(attempt, downgrade ? BeforeBucketNormalizer : WithBucketNormalizer, token));
                var error = Assert.IsType<PostgresException>(wrapper.InnerException);
                watch.Stop();
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
                Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(40));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await transaction.RollbackAsync(cleanup.Token);
            }
            Assert.Null(attempt.Database.CurrentTransaction);
            Assert.Equal(lockTimeout, await MigrationScalarAsync(attempt, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(attempt, "SHOW statement_timeout", token));
            Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
        }, downgrade ? WithBucketNormalizer : BeforeBucketNormalizer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BucketPaddingAndNullObjectNameRefuseAtomicallyWithoutRowMutation(bool update)
    {
        await RunBucketMigrationAsync(async (app, context, token) =>
        {
            await SeedBucketPaddingAsync(app, token);
            var before = await BucketMigrationSnapshotAsync(app, context, token);
            var sql = update
                ? """UPDATE public."OrderFile" SET "Bucket"=repeat('A',50)||' ',"ObjectName"=NULL WHERE "ID"=2"""
                : """INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") VALUES (20,1,repeat('A',50)||' ',NULL)""";
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationExecuteAsync(context, sql, token));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
            Assert.Equal("ObjectName", error.ColumnName);
            Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
        });
    }

    private async Task RunBucketMigrationAsync(Func<WebApplicationFactory<Program>, OrderDbContext, CancellationToken, Task> action, string? target = null)
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var authority = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        if (authority.Database is not { } database || !database.StartsWith("owned_", StringComparison.Ordinal)
            || !Guid.TryParseExact(database[6..], "N", out _) || authority.Host is not ("localhost" or "127.0.0.1") || authority.Pooling)
            throw new InvalidOperationException("Require the fixture-owned isolated database.");
        context.Database.SetCommandTimeout(40);
        await context.Database.OpenConnectionAsync(lifetime.Token);
        try
        {
            await MigrateBucketNormalizerAsync(context, target ?? WithBucketNormalizer, lifetime.Token);
            await action(app, context, lifetime.Token);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static Task MigrateBucketNormalizerAsync(DbContext context, string target, CancellationToken token) =>
        context.GetService<IMigrator>().MigrateAsync(target, token);

    private static async Task AssertBucketNormalizerRefusedAsync(WebApplicationFactory<Program> app, OrderDbContext context, string target, CancellationToken token)
    {
        var before = await BucketMigrationSnapshotAsync(app, context, token);
        var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
        var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateBucketNormalizerAsync(context, target, token));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await BucketMigrationSnapshotAsync(app, context, token));
        Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal("True", await MigrationScalarAsync(context, """SELECT to_regclass('pg_temp."__OrderFileBucketPaddingGuard"') IS NULL""", token));
    }

    private sealed record BucketMigrationState(string Rows, string? ExistingSchema, string? OwnedObjects, string History, string OtherDatabase);

    private static async Task<BucketMigrationState> BucketMigrationSnapshotAsync(WebApplicationFactory<Program> app, DbContext context, CancellationToken token) =>
        new(await ProcessStatusSnapshotAsync(app, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'tables',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relowner,c.relrowsecurity,c.relforcerowsecurity,c.relkind,c.relpersistence) ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN ('r','p')),
                    'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated,c.conislocal,c.coninhcount,c.connoinherit,c.conkey) ORDER BY c.conrelid,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                    'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal AND t.tgname <> 'NormalizeOrderFileBucketSource'),
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname <> 'NormalizeOrderFileBucketSource'),
                    'rules',(SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY r.oid) FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class WHERE c.relnamespace='public'::regnamespace),
                    'inheritance',(SELECT jsonb_agg(row_to_json(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid WHERE c.relnamespace='public'::regnamespace))::text
                """, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname = 'NormalizeOrderFileBucketSource'),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t WHERE NOT t.tgisinternal AND (t.tgname = 'NormalizeOrderFileBucketSource' OR t.tgfoid IN (SELECT p.oid FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname = 'NormalizeOrderFileBucketSource'))))::text
                """, token),
            JsonSerializer.Serialize(await context.Database.GetAppliedMigrationsAsync(token)),
            await ProcessStatusOtherDatabaseSnapshotAsync(app, context, token));
}
