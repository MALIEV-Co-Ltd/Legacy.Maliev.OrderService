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
    private static string BeforeProcessStatusNormalizer(string table) => table switch
    {
        "Process" => "20261024190000_NormalizeCatalogTextSourceStorage",
        "OrderStatus" => "20261007120000_RequireOrderStatusSourceStrings",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static string WithProcessStatusNormalizer(string table) => table switch
    {
        "Process" => "20261024200000_NormalizeProcessTextSourceStorage",
        "OrderStatus" => "20261024200000_NormalizeOrderStatusTextSourceStorage",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    public static IEnumerable<object[]> ProcessStatusColumnPreimages
    {
        get
        {
            foreach (var (table, columnName) in new[] { ("Process", "Name"), ("OrderStatus", "Name"), ("OrderStatus", "Description") })
                foreach (var drift in new[] { "type", "default", "nullability", "generated", "check" })
                    foreach (var downgrade in new[] { false, true })
                        yield return [table, columnName, drift, downgrade];
        }
    }

    public static IEnumerable<object[]> ProcessStatusObjectCollisions
    {
        get
        {
            foreach (var table in new[] { "Process", "OrderStatus" })
                foreach (var drift in new[] { "function", "overload", "named-trigger", "foreign-trigger" })
                    yield return [table, drift];
        }
    }

    public static IEnumerable<object[]> ProcessStatusDowngradeCustody
    {
        get
        {
            foreach (var table in new[] { "Process", "OrderStatus" })
                foreach (var drift in new[] { "disabled", "missing", "columns", "kind", "function", "overload", "foreign-dependent", "search-path", "function-owner", "definer", "table-owner" })
                    yield return [table, drift];
        }
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("OrderStatus")]
    public async Task ProcessStatusNormalizerModelRetainsTextRequirednessMetadataAndEveryExistingCheck(string table)
    {
        await RunProcessStatusMigrationAsync(table, async (_, context, token) =>
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                foreach (var field in table == "Process" ? new[] { "Name" } : new[] { "Name", "Description" })
                {
                    var entity = model.FindEntityType(table == "Process" ? typeof(Process).FullName! : typeof(OrderStatus).FullName!);
                    Assert.NotNull(entity);
                    var property = entity.FindProperty(field);
                    Assert.NotNull(property);
                    Assert.Equal(table == "OrderStatus" && field == "Description", property.IsNullable);
                    Assert.Equal(ProcessStatusMaximum(table, field), property.GetMaxLength());
                    Assert.Equal("text", property.GetColumnType());
                    Assert.Null(property.GetDefaultValue());
                    Assert.Null(property.GetDefaultValueSql());
                    Assert.Contains(entity.GetCheckConstraints(), check => check.Name == $"CK_{table}_{field}_SourceLength");
                }
            }
            Assert.Contains(WithProcessStatusNormalizer(table), await context.Database.GetAppliedMigrationsAsync(token));
        });
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("OrderStatus")]
    public async Task ProcessStatusNormalizerUpDownUpPreservesEveryRowVersionAndUnrelatedSchemaIncludingCollations(string table)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            await MigrationExecuteAsync(context, table == "Process"
                ? """ALTER TABLE public."Process" ALTER COLUMN "Name" TYPE text COLLATE pg_catalog."C";"""
                : """ALTER TABLE public."OrderStatus" ALTER COLUMN "Name" TYPE text COLLATE pg_catalog."POSIX", ALTER COLUMN "Description" TYPE text COLLATE pg_catalog."C";""", token);
            var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            await MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token);
            var upgraded = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
            Assert.NotEqual(before.OwnedObjects, upgraded.OwnedObjects);
            await MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token);
            Assert.Equal(upgraded, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            await MigrateProcessStatusNormalizerAsync(context, BeforeProcessStatusNormalizer(table), token);
            Assert.Equal(before, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            await MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token);
            Assert.Equal(upgraded, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [MemberData(nameof(ProcessStatusColumnPreimages))]
    public async Task ProcessStatusNormalizerRefusesColumnDriftInEitherDirectionWithoutChangingRowsOrSchema(string table, string field, string drift, bool downgrade)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var check = $"CK_{table}_{field}_SourceLength";
            var function = ProcessStatusFunction(table);
            var columns = table == "Process" ? "\"Name\"" : "\"Name\",\"Description\"";
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"DROP TRIGGER \"{function}\" ON public.\"{table}\"", token);
            await MigrationExecuteAsync(context, drift switch
            {
                "type" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" TYPE character varying({ProcessStatusMaximum(table, field)})",
                "default" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" SET DEFAULT 'unexpected'",
                "nullability" => $"ALTER TABLE public.\"{table}\" ALTER COLUMN \"{field}\" {(field == "Name" ? "DROP NOT NULL" : "SET NOT NULL")}",
                "generated" => $$"""
                    ALTER TABLE public."{{table}}" DROP CONSTRAINT "{{check}}";
                    ALTER TABLE public."{{table}}" DROP COLUMN "{{field}}";
                    ALTER TABLE public."{{table}}" ADD COLUMN "{{field}}" text GENERATED ALWAYS AS ('retained'::text) STORED{{(field == "Name" ? " NOT NULL" : "")}};
                    ALTER TABLE public."{{table}}" ADD CONSTRAINT "{{check}}" CHECK
                        (char_length("{{field}}") + char_length(regexp_replace("{{field}}" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= {{ProcessStatusMaximum(table, field)}});
                    """,
                "check" => $"ALTER TABLE public.\"{table}\" DROP CONSTRAINT \"{check}\"; ALTER TABLE public.\"{table}\" ADD CONSTRAINT \"{check}\" CHECK (true)",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            if ((drift is "generated" or "type") && downgrade)
                await MigrationExecuteAsync(context, $"CREATE TRIGGER \"{function}\" BEFORE INSERT OR UPDATE OF {columns} ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()", token);
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, downgrade ? BeforeProcessStatusNormalizer(table) : WithProcessStatusNormalizer(table), token);
        }, downgrade ? WithProcessStatusNormalizer(table) : BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [MemberData(nameof(ProcessStatusObjectCollisions))]
    public async Task ProcessStatusNormalizerUpgradeRefusesEveryNamedOverloadAndForeignTriggerCollision(string table, string drift)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var function = ProcessStatusFunction(table);
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
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, WithProcessStatusNormalizer(table), token);
        }, BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [MemberData(nameof(ProcessStatusDowngradeCustody))]
    public async Task ProcessStatusNormalizerDowngradeRefusesChangedOrSharedCustodyWithoutRemovingAnyObject(string table, string drift)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var function = ProcessStatusFunction(table);
            var columns = table == "Process" ? "\"Name\"" : "\"Name\",\"Description\"";
            await MigrationExecuteAsync(context, drift switch
            {
                "disabled" => $"ALTER TABLE public.\"{table}\" DISABLE TRIGGER \"{function}\"",
                "missing" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"",
                "columns" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"; CREATE TRIGGER \"{function}\" BEFORE INSERT OR UPDATE OF \"CreatedDate\" ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "kind" => $"DROP TRIGGER \"{function}\" ON public.\"{table}\"; CREATE TRIGGER \"{function}\" BEFORE UPDATE OF {columns} ON public.\"{table}\" FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "function" => $"CREATE OR REPLACE FUNCTION public.\"{function}\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "overload" => $"CREATE FUNCTION public.\"{function}\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "foreign-dependent" => $"CREATE TABLE public.foreign_normalizer_target (\"Name\" text); CREATE TRIGGER foreign_catalog_use BEFORE UPDATE OF \"Name\" ON public.foreign_normalizer_target FOR EACH ROW EXECUTE FUNCTION public.\"{function}\"()",
                "search-path" => $"ALTER FUNCTION public.\"{function}\"() SET search_path = public,pg_catalog",
                "function-owner" => $"ALTER FUNCTION public.\"{function}\"() OWNER TO pg_database_owner",
                "definer" => $"ALTER FUNCTION public.\"{function}\"() SECURITY DEFINER",
                "table-owner" => $"ALTER TABLE public.\"{table}\" OWNER TO pg_database_owner",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, BeforeProcessStatusNormalizer(table), token);
        });
    }

    [Theory]
    [InlineData("Process", "row-security")]
    [InlineData("OrderStatus", "row-security")]
    [InlineData("Process", "rule")]
    [InlineData("OrderStatus", "rule")]
    [InlineData("Process", "inheritance")]
    [InlineData("OrderStatus", "inheritance")]
    public async Task ProcessStatusNormalizerRefusesForeignTableSemanticsBeforeInstallingAnything(string table, string drift)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "row-security" => $"ALTER TABLE public.\"{table}\" ENABLE ROW LEVEL SECURITY",
                "rule" => $"CREATE RULE foreign_catalog_rule AS ON INSERT TO public.\"{table}\" DO INSTEAD NOTHING",
                "inheritance" => $"CREATE TABLE public.catalog_child () INHERITS (public.\"{table}\")",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, WithProcessStatusNormalizer(table), token);
        }, BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("OrderStatus")]
    public async Task ProcessStatusNormalizerRowAdmissionRefuses10001AndAccepts10000InBothDirections(string table)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            await MigrationExecuteAsync(context, table == "Process"
                ? """INSERT INTO public."Process" ("ID","Name","CategoryID") SELECT value,'Retained',1 FROM generate_series(3,10001) value"""
                : """INSERT INTO public."OrderStatus" ("ID","Name") SELECT value,'Retained' FROM generate_series(3,10001) value""", token);
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, WithProcessStatusNormalizer(table), token);
            await MigrationExecuteAsync(context, $"DELETE FROM public.\"{table}\" WHERE \"ID\"=10001", token);
            var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            await MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token);
            var upgraded = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
            await MigrationExecuteAsync(context, table == "Process"
                ? """INSERT INTO public."Process" ("ID","Name","CategoryID") VALUES (10001,'Retained',1)"""
                : """INSERT INTO public."OrderStatus" ("ID","Name") VALUES (10001,'Retained')""", token);
            await AssertProcessStatusNormalizerRefusedAsync(app, context, table, BeforeProcessStatusNormalizer(table), token);
            await MigrationExecuteAsync(context, $"DELETE FROM public.\"{table}\" WHERE \"ID\"=10001", token);
            var afterDelete = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            await MigrateProcessStatusNormalizerAsync(context, BeforeProcessStatusNormalizer(table), token);
            var downgraded = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            Assert.Equal(afterDelete.Rows, downgraded.Rows);
            Assert.Equal(afterDelete.ExistingSchema, downgraded.ExistingSchema);
            Assert.Equal(afterDelete.OtherDatabase, downgraded.OtherDatabase);
        }, BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("OrderStatus")]
    public async Task ProcessStatusNormalizerPreservesAnExistingForeignTemporaryGuardTable(string table)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            await MigrationExecuteAsync(context, $$"""
                CREATE TEMP TABLE "__{{table}}TextPaddingGuard" ("Marker" text);
                INSERT INTO "__{{table}}TextPaddingGuard" VALUES ('retained foreign guard');
                """, token);
            var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token));
            Assert.Equal("P0001", error.SqlState);
            Assert.Equal(before, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            Assert.Equal("retained foreign guard", await MigrationScalarAsync(context, $"SELECT \"Marker\" FROM pg_temp.\"__{table}TextPaddingGuard\"", token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessStatusPairedFieldsNormalizeTogetherAndOtherOverflowRefusesAtomically(bool update)
    {
        const string table = "OrderStatus";
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var id = update ? 1 : 20;
            var before = await ProcessStatusSnapshotAsync(app, token, table, id);
            var dates = update ? await MigrationScalarAsync(context, """SELECT (to_jsonb(t)-'Name'-'Description')::text FROM public."OrderStatus" t WHERE "ID"=1""", token) : null;
            await MigrationExecuteAsync(context, update
                ? """UPDATE public."OrderStatus" SET "Name"=repeat('A',50)||' ',"Description"=repeat('ก',100)||'  ' WHERE "ID"=1"""
                : """INSERT INTO public."OrderStatus" ("ID","Name","Description") VALUES (20,repeat('A',50)||' ',repeat('ก',100)||'  ')""", token);
            Assert.Equal(new string('A', 50), await ProcessStatusValueAsync(context, table, "Name", id, token));
            Assert.Equal(new string('ก', 100), await ProcessStatusValueAsync(context, table, "Description", id, token));
            if (update) Assert.Equal(dates, await MigrationScalarAsync(context, """SELECT (to_jsonb(t)-'Name'-'Description')::text FROM public."OrderStatus" t WHERE "ID"=1""", token));
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token, table, id));
            foreach (var refusedField in new[] { "Name", "Description" })
            {
                var after = await ProcessStatusMigrationSnapshotAsync(app, context, token);
                var name = refusedField == "Name" ? "repeat('A',50)||chr(160)" : "repeat('A',50)||' '";
                var description = refusedField == "Description" ? "repeat('ก',100)||chr(160)" : "repeat('ก',100)||' '";
                var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationExecuteAsync(context,
                    update ? $"UPDATE public.\"OrderStatus\" SET \"Name\"={name},\"Description\"={description} WHERE \"ID\"={id}"
                        : $"INSERT INTO public.\"OrderStatus\" (\"ID\",\"Name\",\"Description\") VALUES (21,{name},{description})", token));
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal($"CK_OrderStatus_{refusedField}_SourceLength", error.ConstraintName);
                Assert.Equal(after, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            }
        });
    }

    [Theory]
    [InlineData("Process", false)]
    [InlineData("Process", true)]
    [InlineData("OrderStatus", false)]
    [InlineData("OrderStatus", true)]
    public async Task ProcessStatusNormalizerLockDeadlineRollsBackWithoutChangingEitherDatabase(string table, bool downgrade)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            await using DbContext singleAttempt = table switch
            {
                "Process" => new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options),
                "OrderStatus" => new OrderStatusDbContext(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options),
                _ => throw new ArgumentOutOfRangeException(nameof(table)),
            };
            Assert.False(singleAttempt.Database.CreateExecutionStrategy().RetriesOnFailure);
            singleAttempt.Database.SetCommandTimeout(40);
            await singleAttempt.Database.OpenConnectionAsync(token);
            var lockTimeout = await MigrationScalarAsync(singleAttempt, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(singleAttempt, "SHOW statement_timeout", token);
            await using var blocker = new NpgsqlConnection(context.Database.GetConnectionString());
            await blocker.OpenAsync(token);
            await using var transaction = await blocker.BeginTransactionAsync(token);
            try
            {
                await using var command = new NpgsqlCommand($"LOCK TABLE public.\"{table}\" IN ACCESS EXCLUSIVE MODE", blocker, transaction) { CommandTimeout = 40 };
                await command.ExecuteNonQueryAsync(token);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateProcessStatusNormalizerAsync(singleAttempt,
                    downgrade ? BeforeProcessStatusNormalizer(table) : WithProcessStatusNormalizer(table), token));
                var failure = Assert.IsType<PostgresException>(wrapper.InnerException);
                watch.Stop();
                Assert.Equal(PostgresErrorCodes.LockNotAvailable, failure.SqlState);
                Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(40));
            }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await transaction.RollbackAsync(cleanup.Token);
            }
            Assert.Null(singleAttempt.Database.CurrentTransaction);
            Assert.Equal(lockTimeout, await MigrationScalarAsync(singleAttempt, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(singleAttempt, "SHOW statement_timeout", token));
            Assert.Equal("True", await MigrationScalarAsync(singleAttempt, $"SELECT to_regclass('pg_temp.\"__{table}TextPaddingGuard\"') IS NULL", token));
            Assert.Equal(before, await ProcessStatusMigrationSnapshotAsync(app, context, token));
            await MigrateProcessStatusNormalizerAsync(context, downgrade ? BeforeProcessStatusNormalizer(table) : WithProcessStatusNormalizer(table), token);
            var after = await ProcessStatusMigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, after.Rows);
            Assert.Equal(before.ExistingSchema, after.ExistingSchema);
            Assert.Equal(before.OtherDatabase, after.OtherDatabase);
        }, downgrade ? WithProcessStatusNormalizer(table) : BeforeProcessStatusNormalizer(table));
    }

    [Theory]
    [InlineData("Process")]
    [InlineData("OrderStatus")]
    public async Task ProcessStatusNormalizerNeverDropsAPublicGuardNameCollisionWhenTemporarySchemaIsLast(string table)
    {
        await RunProcessStatusMigrationAsync(table, async (app, context, token) =>
        {
            await SeedProcessStatusPaddingAsync(app, token);
            var originalPath = await MigrationScalarAsync(context, "SHOW search_path", token);
            Assert.NotNull(originalPath);
            try
            {
                await MigrationExecuteAsync(context, $$"""
                    CREATE TABLE public."__{{table}}TextPaddingGuard" ("Marker" text NOT NULL);
                    INSERT INTO public."__{{table}}TextPaddingGuard" VALUES ('retained public sentinel');
                    SET search_path = public,pg_temp;
                    """, token);
                var explicitPath = await MigrationScalarAsync(context, "SHOW search_path", token);
                var sentinelSql = $$"""
                    SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."Marker")::text
                    FROM public."__{{table}}TextPaddingGuard" t
                    """;
                var sentinel = await MigrationScalarAsync(context, sentinelSql, token);
                Assert.NotNull(sentinel);
                var identitySql = $"""SELECT to_regclass('public."__{table}TextPaddingGuard"')::oid::text""";
                var publicOid = await MigrationScalarAsync(context, identitySql, token);
                Assert.NotNull(publicOid);
                var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
                await MigrateProcessStatusNormalizerAsync(context, WithProcessStatusNormalizer(table), token);
                var upgraded = await ProcessStatusMigrationSnapshotAsync(app, context, token);
                Assert.Equal(before.Rows, upgraded.Rows);
                Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
                Assert.Equal(before.OtherDatabase, upgraded.OtherDatabase);
                Assert.Equal(sentinel, await MigrationScalarAsync(context, sentinelSql, token));
                Assert.Equal(publicOid, await MigrationScalarAsync(context, identitySql, token));
                Assert.Equal(explicitPath, await MigrationScalarAsync(context, "SHOW search_path", token));
                await MigrateProcessStatusNormalizerAsync(context, BeforeProcessStatusNormalizer(table), token);
                Assert.Equal(before, await ProcessStatusMigrationSnapshotAsync(app, context, token));
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
        }, BeforeProcessStatusNormalizer(table));
    }

    private async Task RunProcessStatusMigrationAsync(string table, Func<WebApplicationFactory<Program>, DbContext, CancellationToken, Task> action, string? target = null)
    {
        _ = BeforeProcessStatusNormalizer(table);
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var context = ProcessStatusDatabase(scope.ServiceProvider, table);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var authority = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        if (authority.Database is not { } database || !database.StartsWith("owned_", StringComparison.Ordinal)
            || !Guid.TryParseExact(database[6..], "N", out _) || authority.Host is not ("localhost" or "127.0.0.1") || authority.Pooling)
            throw new InvalidOperationException("Require the fixture-owned isolated database.");
        context.Database.SetCommandTimeout(40);
        await context.Database.OpenConnectionAsync(lifetime.Token);
        try
        {
            await MigrateProcessStatusNormalizerAsync(context, target ?? WithProcessStatusNormalizer(table), lifetime.Token);
            await action(app, context, lifetime.Token);
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private static Task MigrateProcessStatusNormalizerAsync(DbContext context, string target, CancellationToken token) =>
        context.GetService<IMigrator>().MigrateAsync(target, token);

    private static string ProcessStatusFunction(string table) => table switch
    {
        "Process" => "NormalizeProcessTextSource",
        "OrderStatus" => "NormalizeOrderStatusTextSource",
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static async Task AssertProcessStatusNormalizerRefusedAsync(WebApplicationFactory<Program> app, DbContext context, string table, string target, CancellationToken token)
    {
        var before = await ProcessStatusMigrationSnapshotAsync(app, context, token);
        var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
        var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateProcessStatusNormalizerAsync(context, target, token));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await ProcessStatusMigrationSnapshotAsync(app, context, token));
        Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal("True", await MigrationScalarAsync(context, $"SELECT to_regclass('pg_temp.\"__{table}TextPaddingGuard\"') IS NULL", token));
    }

    private sealed record ProcessStatusMigrationSnapshot(string Rows, string? ExistingSchema, string? OwnedObjects, string History, string OtherDatabase);

    private static async Task<ProcessStatusMigrationSnapshot> ProcessStatusMigrationSnapshotAsync(WebApplicationFactory<Program> app, DbContext context, CancellationToken token) =>
        new(await ProcessStatusSnapshotAsync(app, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'tables',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relowner,c.relrowsecurity,c.relforcerowsecurity,c.relkind,c.relpersistence) ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN ('r','p')),
                    'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated,c.conislocal,c.coninhcount,c.connoinherit,c.conkey) ORDER BY c.conrelid,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                    'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal AND t.tgname NOT IN ('NormalizeProcessTextSource','NormalizeOrderStatusTextSource')),
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname NOT IN ('NormalizeProcessTextSource','NormalizeOrderStatusTextSource')),
                    'rules',(SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY r.oid) FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class WHERE c.relnamespace='public'::regnamespace),
                    'inheritance',(SELECT jsonb_agg(row_to_json(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid WHERE c.relnamespace='public'::regnamespace))::text
                """, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeProcessTextSource','NormalizeOrderStatusTextSource')),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t WHERE NOT t.tgisinternal AND (t.tgname IN ('NormalizeProcessTextSource','NormalizeOrderStatusTextSource') OR t.tgfoid IN (SELECT p.oid FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeProcessTextSource','NormalizeOrderStatusTextSource')))))::text
                """, token),
            JsonSerializer.Serialize(await context.Database.GetAppliedMigrationsAsync(token)),
            await ProcessStatusOtherDatabaseSnapshotAsync(app, context, token));

    private static async Task<string> ProcessStatusOtherDatabaseSnapshotAsync(WebApplicationFactory<Program> app, DbContext selected, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var other = ProcessStatusDatabase(scope.ServiceProvider, selected is OrderDbContext ? "OrderStatus" : "Process");
        await other.Database.OpenConnectionAsync(token);
        try
        {
            var schema = await MigrationScalarAsync(other, """
                SELECT jsonb_build_object(
                    'tables',(SELECT jsonb_agg(jsonb_build_array(c.relname,c.relowner,c.relrowsecurity,c.relforcerowsecurity,c.relkind,c.relpersistence) ORDER BY c.relname) FROM pg_class c WHERE c.relnamespace='public'::regnamespace AND c.relkind IN ('r','p')),
                    'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated,c.conislocal,c.coninhcount,c.connoinherit,c.conkey) ORDER BY c.conrelid,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                    'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                    'functions',(SELECT jsonb_agg(jsonb_build_array(pg_get_functiondef(p.oid),p.proowner) ORDER BY p.proname,pg_get_function_identity_arguments(p.oid)) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgrelid,t.tgname) FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid WHERE c.relnamespace='public'::regnamespace AND NOT t.tgisinternal),
                    'rules',(SELECT jsonb_agg(pg_get_ruledef(r.oid) ORDER BY r.oid) FROM pg_rewrite r JOIN pg_class c ON c.oid=r.ev_class WHERE c.relnamespace='public'::regnamespace),
                    'inheritance',(SELECT jsonb_agg(row_to_json(i) ORDER BY i.inhrelid,i.inhseqno) FROM pg_inherits i JOIN pg_class c ON c.oid=i.inhrelid WHERE c.relnamespace='public'::regnamespace))::text
                """, token);
            Assert.NotNull(schema);
            return schema + "\n" + JsonSerializer.Serialize(await other.Database.GetAppliedMigrationsAsync(token));
        }
        finally { await other.Database.CloseConnectionAsync(); }
    }
}
