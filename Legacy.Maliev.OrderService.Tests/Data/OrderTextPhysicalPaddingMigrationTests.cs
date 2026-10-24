using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    private const string BeforeNormalizer = "20261024130000_EnforceTrackingSourceStorage";
    private const string WithNormalizer = "20261024180000_NormalizeOrderTextSourceStorage";

    [Fact]
    public async Task NormalizerModelRetainsAcceptedTextDefaultsAndChecksWithoutPendingChanges()
    {
        await RunMigrationAsync(async (_, context, token) =>
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                var entity = model.FindEntityType(typeof(Order).FullName!);
                Assert.NotNull(entity);
                foreach (var field in new[] { "Name", "Description" })
                {
                    var property = entity.FindProperty(field);
                    Assert.NotNull(property);
                    Assert.True(property.IsNullable);
                    Assert.Equal("text", property.GetColumnType());
                    Assert.Equal(Maximum(field), property.GetMaxLength());
                    Assert.Contains(entity.GetCheckConstraints(), check => check.Name == $"CK_Order_{field}_SourceLength");
                }
                Assert.Equal("unnamed", entity.FindProperty("Name")!.GetDefaultValue());
            }
            Assert.Contains(WithNormalizer, await context.Database.GetAppliedMigrationsAsync(token));
        });
    }

    [Fact]
    public async Task NormalizerUpDownUpPreservesEveryRowVersionAndExistingSchemaIncludingCollations()
    {
        await RunMigrationAsync(async (app, context, token) =>
        {
            await SeedMigrationAsync(app, context, token);
            await MigrationExecuteAsync(context, """
                ALTER TABLE public."Order" ALTER COLUMN "Name" TYPE text COLLATE pg_catalog."C",
                    ALTER COLUMN "Description" TYPE text COLLATE pg_catalog."POSIX";
                """, token);
            var before = await MigrationSnapshotAsync(app, context, token);
            var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
            await MigrateNormalizerAsync(context, WithNormalizer, token);
            var upgraded = await MigrationSnapshotAsync(app, context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            Assert.Equal(before.ExistingSchema, upgraded.ExistingSchema);
            Assert.NotEqual(before.OwnedObjects, upgraded.OwnedObjects);
            await MigrateNormalizerAsync(context, WithNormalizer, token);
            Assert.Equal(upgraded, await MigrationSnapshotAsync(app, context, token));
            await MigrateNormalizerAsync(context, BeforeNormalizer, token);
            Assert.Equal(before, await MigrationSnapshotAsync(app, context, token));
            await MigrateNormalizerAsync(context, WithNormalizer, token);
            Assert.Equal(upgraded, await MigrationSnapshotAsync(app, context, token));
            Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, BeforeNormalizer);
    }

    [Theory]
    [InlineData("Name-type")]
    [InlineData("Description-type")]
    [InlineData("Name-default")]
    [InlineData("Description-default")]
    [InlineData("Name-nullability")]
    [InlineData("Description-nullability")]
    [InlineData("Name-generated")]
    [InlineData("Name-check")]
    [InlineData("Description-check")]
    [InlineData("function")]
    [InlineData("trigger")]
    public async Task NormalizerUpgradeRefusesUnexpectedPreimagesWithoutTouchingAnyRowsOrSchema(string drift)
    {
        await RunMigrationAsync(async (app, context, token) =>
        {
            await SeedMigrationAsync(app, context, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "Name-type" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Name\" TYPE character varying(100)",
                "Description-type" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Description\" TYPE character varying(250)",
                "Name-default" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Name\" SET DEFAULT 'unexpected'",
                "Description-default" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Description\" SET DEFAULT 'unexpected'",
                "Name-nullability" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Name\" SET NOT NULL",
                "Description-nullability" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Description\" SET NOT NULL",
                "Name-generated" => """
                    ALTER TABLE public."Order" DROP CONSTRAINT "CK_Order_Name_SourceLength";
                    ALTER TABLE public."Order" DROP COLUMN "Name";
                    ALTER TABLE public."Order" ADD COLUMN "Name" text GENERATED ALWAYS AS ('unnamed'::text) STORED;
                    ALTER TABLE public."Order" ADD CONSTRAINT "CK_Order_Name_SourceLength" CHECK
                        (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100);
                    """,
                "Name-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_Name_SourceLength\"; ALTER TABLE public.\"Order\" ADD CONSTRAINT \"CK_Order_Name_SourceLength\" CHECK (true)",
                "Description-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_Description_SourceLength\"; ALTER TABLE public.\"Order\" ADD CONSTRAINT \"CK_Order_Description_SourceLength\" CHECK (true)",
                "function" => "CREATE FUNCTION public.\"NormalizeOrderTextSource\"() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                "trigger" => """
                    CREATE FUNCTION public.text_collision() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END;';
                    CREATE TRIGGER "NormalizeOrderTextSource" BEFORE INSERT ON public."Order"
                        FOR EACH ROW EXECUTE FUNCTION public.text_collision();
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertNormalizerRefusedAsync(app, context, WithNormalizer, token);
        }, BeforeNormalizer);
    }

    [Theory]
    [InlineData("disabled-trigger")]
    [InlineData("missing-trigger")]
    [InlineData("wrong-columns")]
    [InlineData("function")]
    [InlineData("overload")]
    [InlineData("foreign-dependent-trigger")]
    [InlineData("Name-check")]
    [InlineData("Description-check")]
    [InlineData("Name-default")]
    public async Task NormalizerDowngradeRefusesForeignDriftAndPreservesItsCustody(string drift)
    {
        await RunMigrationAsync(async (app, context, token) =>
        {
            await SeedMigrationAsync(app, context, token);
            await MigrationExecuteAsync(context, drift switch
            {
                "disabled-trigger" => "ALTER TABLE public.\"Order\" DISABLE TRIGGER \"NormalizeOrderTextSource\"",
                "missing-trigger" => "DROP TRIGGER \"NormalizeOrderTextSource\" ON public.\"Order\"",
                "wrong-columns" => "DROP TRIGGER \"NormalizeOrderTextSource\" ON public.\"Order\"; CREATE TRIGGER \"NormalizeOrderTextSource\" BEFORE INSERT OR UPDATE OF \"Name\" ON public.\"Order\" FOR EACH ROW EXECUTE FUNCTION public.\"NormalizeOrderTextSource\"()",
                "function" => "CREATE OR REPLACE FUNCTION public.\"NormalizeOrderTextSource\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "overload" => "CREATE FUNCTION public.\"NormalizeOrderTextSource\"(integer) RETURNS integer LANGUAGE sql AS 'SELECT $1'",
                "foreign-dependent-trigger" => "CREATE TRIGGER foreign_text_normalizer BEFORE UPDATE OF \"Name\" ON public.\"Order\" FOR EACH ROW EXECUTE FUNCTION public.\"NormalizeOrderTextSource\"()",
                "Name-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_Name_SourceLength\"",
                "Description-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_Description_SourceLength\"",
                "Name-default" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"Name\" SET DEFAULT 'unexpected'",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertNormalizerRefusedAsync(app, context, BeforeNormalizer, token);
        });
    }

    [Fact]
    public async Task NormalizerRowAdmissionBoundsBothDirectionsWithoutRewritingRetainedRows()
    {
        await RunMigrationAsync(async (app, context, token) =>
        {
            await SeedMigrationAsync(app, context, token);
            await MigrationExecuteAsync(context, """
                INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment")
                    SELECT value,1,1,0,false,false,false FROM generate_series(3,10001) value;
                """, token);
            await AssertNormalizerRefusedAsync(app, context, WithNormalizer, token);
            await MigrationExecuteAsync(context, "DELETE FROM public.\"Order\" WHERE \"ID\"=10001", token);
            var before = await MigrationSnapshotAsync(app, context, token);
            await MigrateNormalizerAsync(context, WithNormalizer, token);
            Assert.Equal(before.Rows, (await MigrationSnapshotAsync(app, context, token)).Rows);
            await MigrationExecuteAsync(context, """
                INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment")
                    VALUES (10001,1,1,0,false,false,false);
                """, token);
            await AssertNormalizerRefusedAsync(app, context, BeforeNormalizer, token);
            await MigrationExecuteAsync(context, "DELETE FROM public.\"Order\" WHERE \"ID\"=10001", token);
            var afterDelete = await MigrationSnapshotAsync(app, context, token);
            await MigrateNormalizerAsync(context, BeforeNormalizer, token);
            Assert.Equal(afterDelete.Rows, (await MigrationSnapshotAsync(app, context, token)).Rows);
        }, BeforeNormalizer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothPhysicalFieldsNormalizeTogetherAndRejectOtherOverflowAtomically(bool update)
    {
        await RunMigrationAsync(async (app, context, token) =>
        {
            await SeedMigrationAsync(app, context, token);
            var id = update ? 1 : 20;
            var beforeOthers = await SnapshotAsync(app, token, id);
            var original = update ? await context.Orders.AsNoTracking().SingleAsync(row => row.Id == id, token) : null;
            await MigrationExecuteAsync(context, update
                ? "UPDATE public.\"Order\" SET \"Name\"=repeat('A',100)||' ',\"Description\"=repeat('ก',250)||'  ' WHERE \"ID\"=1"
                : "INSERT INTO public.\"Order\" (\"ID\",\"ProcessID\",\"Quantity\",\"Manufactured\",\"AllowSocialMedia\",\"AllowCancellation\",\"AllowPayment\",\"Name\",\"Description\") VALUES (20,1,1,0,false,false,false,repeat('A',100)||' ',repeat('ก',250)||'  ')", token);
            var row = await context.Orders.AsNoTracking().SingleAsync(value => value.Id == id, token);
            Assert.Equal(new string('A', 100), row.Name);
            Assert.Equal(new string('ก', 250), row.Description);
            if (original is not null)
            {
                original.Name = row.Name;
                original.Description = row.Description;
                Assert.Equal(System.Text.Json.JsonSerializer.Serialize(original), System.Text.Json.JsonSerializer.Serialize(row));
            }
            Assert.Equal(beforeOthers, await SnapshotAsync(app, token, id));
            var beforeRefusal = await MigrationSnapshotAsync(app, context, token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrationExecuteAsync(context,
                $"UPDATE public.\"Order\" SET \"Name\"=repeat('A',100)||' ',\"Description\"=repeat('ก',250)||chr(160) WHERE \"ID\"={id}", token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("CK_Order_Description_SourceLength", error.ConstraintName);
            Assert.Equal(beforeRefusal, await MigrationSnapshotAsync(app, context, token));
        });
    }

    private async Task RunMigrationAsync(Func<WebApplicationFactory<Program>, OrderDbContext, CancellationToken, Task> action, string? target = null)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        context.Database.SetCommandTimeout(30);
        await context.Database.OpenConnectionAsync(lifetime.Token);
        try
        {
            await MigrateNormalizerAsync(context, target ?? WithNormalizer, lifetime.Token);
            await action(app, context, lifetime.Token);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task MigrateNormalizerAsync(OrderDbContext context, string target, CancellationToken token) =>
        context.GetService<IMigrator>().MigrateAsync(target, token);

    private static async Task AssertNormalizerRefusedAsync(WebApplicationFactory<Program> app, OrderDbContext context, string target, CancellationToken token)
    {
        var before = await MigrationSnapshotAsync(app, context, token);
        var lockTimeout = await MigrationScalarAsync(context, "SHOW lock_timeout", token);
        var statementTimeout = await MigrationScalarAsync(context, "SHOW statement_timeout", token);
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateNormalizerAsync(context, target, token));
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await MigrationSnapshotAsync(app, context, token));
        Assert.Equal(lockTimeout, await MigrationScalarAsync(context, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await MigrationScalarAsync(context, "SHOW statement_timeout", token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal("True", await MigrationScalarAsync(context, "SELECT to_regclass('pg_temp.\"__OrderTextPaddingGuard\"') IS NULL", token));
    }

    private static async Task SeedMigrationAsync(WebApplicationFactory<Program> app, OrderDbContext context, CancellationToken token)
    {
        await SeedAsync(app, token);
        await MigrationExecuteAsync(context, """
            UPDATE public."Order" SET "OperationKey"='retained-'||"ID"::text;
            INSERT INTO public."OrderDeletionIntent" ("OrderId","DeletionId","RequestedAtUtc","AttemptCount","NextAttemptAtUtc")
                VALUES (2,'22222222-2222-2222-2222-222222222222','2026-01-02T03:04:05Z',0,'2026-01-02T03:04:05Z');
            """, token);
    }

    private static async Task MigrationExecuteAsync(DbContext context, string sql, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string?> MigrationScalarAsync(DbContext context, string sql, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record MigrationSnapshot(string Rows, string? ExistingSchema, string? OwnedObjects, string Migrations);

    private static async Task<MigrationSnapshot> MigrationSnapshotAsync(WebApplicationFactory<Program> app, OrderDbContext context, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await statuses.Database.OpenConnectionAsync(token);
        string? statusRows;
        try
        {
            statusRows = await MigrationScalarAsync(statuses, """
                SELECT jsonb_build_object(
                    'statuses',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatus" t),
                    'transitions',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatusHasPossibleStatus" t),
                    'history',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatusHistory" t))::text
                """, token);
        }
        finally
        {
            await statuses.Database.CloseConnectionAsync();
        }
        var orderRows = await MigrationScalarAsync(context, """
            SELECT jsonb_build_object(
                'orders',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Order" t),
                'categories',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Category" t),
                'processes',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Process" t),
                'formats',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."FileFormat" t),
                'files',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderFile" t),
                'intents',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."OrderId") FROM public."OrderDeletionIntent" t))::text
            """, token);
        return new MigrationSnapshot(orderRows + "\n" + statusRows,
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public'),
                    'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated) ORDER BY c.conrelid,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace),
                    'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(t.tgname,pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgname) FROM pg_trigger t WHERE t.tgrelid='public."Order"'::regclass AND NOT t.tgisinternal AND t.tgname<>'NormalizeOrderTextSource'),
                    'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.proname) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeOrderTrackingSource','text_collision')))::text
                """, token),
            await MigrationScalarAsync(context, """
                SELECT jsonb_build_object(
                    'triggers',(SELECT jsonb_agg(jsonb_build_array(pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgname) FROM pg_trigger t WHERE t.tgrelid='public."Order"'::regclass AND t.tgname='NormalizeOrderTextSource'),
                    'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.oid) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname='NormalizeOrderTextSource'))::text
                """, token),
            System.Text.Json.JsonSerializer.Serialize(await context.Database.GetAppliedMigrationsAsync(token)));
    }
}
