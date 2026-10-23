using System.Text.Json;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Tests.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class OrderTrackingSourceMigrationTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const string Previous = "20261018120000_EnforceNullableOrderTextSourceLengths";
    private const string Target = "20261024130000_EnforceTrackingSourceStorage";

    [Fact]
    public async Task ModelAndSnapshotRetainNullableTrackingAndIndependentUtf16Check()
    {
        await RunAsync(async (context, token) =>
        {
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                var entity = model.FindEntityType(typeof(Order).FullName!);
                Assert.NotNull(entity);
                var tracking = entity.FindProperty(nameof(Order.TrackingNumber));
                Assert.NotNull(tracking);
                Assert.True(tracking.IsNullable);
                Assert.Equal(250, tracking.GetMaxLength());
                Assert.Equal("text", tracking.GetColumnType());
                Assert.Contains(entity.GetCheckConstraints(), value => value.Name == "CK_Order_TrackingNumber_SourceLength");
            }
            Assert.Contains(Target, await context.Database.GetAppliedMigrationsAsync(token));
        });
    }

    [Fact]
    public async Task UpDownUpPreservesRowsSchemaDefaultsAndSessionTimeouts()
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await SetTrackingAsync(context, string.Concat(Enumerable.Repeat("😀", 124)) + "  ", token);
            var before = await SnapshotAsync(context, token);
            var lockTimeout = await ScalarAsync(context, "SHOW lock_timeout", token);
            var statementTimeout = await ScalarAsync(context, "SHOW statement_timeout", token);
            await MigrateAsync(context, Target, token);
            var upgraded = await SnapshotAsync(context, token);
            Assert.Equal(before.Rows, upgraded.Rows);
            await MigrateAsync(context, Target, token);
            Assert.Equal(upgraded, await SnapshotAsync(context, token));
            await MigrateAsync(context, Previous, token);
            Assert.Equal(before, await SnapshotAsync(context, token));
            await MigrateAsync(context, Target, token);
            Assert.Equal(upgraded, await SnapshotAsync(context, token));
            Assert.Equal(lockTimeout, await ScalarAsync(context, "SHOW lock_timeout", token));
            Assert.Equal(statementTimeout, await ScalarAsync(context, "SHOW statement_timeout", token));
            Assert.Null(context.Database.CurrentTransaction);
        }, Previous);
    }

    [Fact]
    public async Task RetainedSupplementaryOverflowRefusesUpgradeWithoutRewritingRows()
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await SetTrackingAsync(context, string.Concat(Enumerable.Repeat("😀", 126)), token);
            await AssertRefusedAsync(context, Target, token);
        }, Previous);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("default")]
    [InlineData("check")]
    [InlineData("function")]
    [InlineData("trigger")]
    public async Task UnexpectedUpgradePreimageRefusesWithoutChangingRowsOrSchema(string drift)
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await ExecuteAsync(context, drift switch
            {
                "type" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"TrackingNumber\" TYPE text",
                "default" => "ALTER TABLE public.\"Order\" ALTER COLUMN \"TrackingNumber\" SET DEFAULT 'unexpected'",
                "check" => "ALTER TABLE public.\"Order\" ADD CONSTRAINT \"CK_Order_TrackingNumber_SourceLength\" CHECK (true)",
                "function" => "CREATE FUNCTION public.\"NormalizeOrderTrackingSource\"() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
                "trigger" => """
                    CREATE FUNCTION public.tracking_collision() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END;';
                    CREATE TRIGGER "NormalizeOrderTrackingSource" BEFORE INSERT ON public."Order"
                        FOR EACH ROW EXECUTE FUNCTION public.tracking_collision();
                    """,
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertRefusedAsync(context, Target, token);
        }, Previous);
    }

    [Theory]
    [InlineData("disabled-trigger")]
    [InlineData("function")]
    [InlineData("missing-check")]
    [InlineData("wrong-check")]
    public async Task ChangedOwnedObjectsRefuseDowngradeWithoutRemovingUnknownSchema(string drift)
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await ExecuteAsync(context, drift switch
            {
                "disabled-trigger" => "ALTER TABLE public.\"Order\" DISABLE TRIGGER \"NormalizeOrderTrackingSource\"",
                "function" => "CREATE OR REPLACE FUNCTION public.\"NormalizeOrderTrackingSource\"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS 'BEGIN RETURN NEW; END;'",
                "missing-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_TrackingNumber_SourceLength\"",
                "wrong-check" => "ALTER TABLE public.\"Order\" DROP CONSTRAINT \"CK_Order_TrackingNumber_SourceLength\"; ALTER TABLE public.\"Order\" ADD CONSTRAINT \"CK_Order_TrackingNumber_SourceLength\" CHECK (true)",
                _ => throw new ArgumentOutOfRangeException(nameof(drift)),
            }, token);
            await AssertRefusedAsync(context, Previous, token);
        });
    }

    [Fact]
    public async Task RowAdmissionRefuses10001ThenAccepts10000WithoutRewriting()
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await ExecuteAsync(context, """
                INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment")
                    SELECT value,1,1,0,false,false,false FROM generate_series(3,10001) value;
                """, token);
            await AssertRefusedAsync(context, Target, token);
            await ExecuteAsync(context, "DELETE FROM public.\"Order\" WHERE \"ID\"=10001", token);
            var before = await SnapshotAsync(context, token);
            await MigrateAsync(context, Target, token);
            Assert.Equal(before.Rows, (await SnapshotAsync(context, token)).Rows);
        }, Previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviousEfMappingRetainsPhysicalPaddingAndRejectsNonspaceOverflow(bool update)
    {
        await RunAsync(async (context, token) =>
        {
            await SeedAsync(context, token);
            await using var old = new PreviousTrackingContext(new DbContextOptionsBuilder<PreviousTrackingContext>()
                .UseNpgsql(context.Database.GetDbConnection(), contextOwnsConnection: false).Options);
            var literal = string.Concat(Enumerable.Repeat("😀", 124)) + "   ";
            var value = new PreviousOrder { Id = update ? 1 : 20, ProcessId = 1, Quantity = 1, TrackingNumber = literal };
            if (update)
            {
                old.Attach(value);
                old.Entry(value).Property(row => row.TrackingNumber).IsModified = true;
            }
            else old.Add(value);
            await old.SaveChangesAsync(token);
            Assert.Equal(literal, value.TrackingNumber);
            Assert.Equal(literal[..250], await ScalarAsync(context, $"SELECT \"TrackingNumber\" FROM public.\"Order\" WHERE \"ID\"={value.Id}", token));
            var before = await SnapshotAsync(context, token);
            if (update) value.TrackingNumber = new string('A', 250) + "\t";
            else old.Add(new PreviousOrder { Id = 21, ProcessId = 1, Quantity = 1, TrackingNumber = new string('A', 250) + "\t" });
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => old.SaveChangesAsync(token));
            var postgres = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.CheckViolation, postgres.SqlState);
            Assert.Equal("CK_Order_TrackingNumber_SourceLength", postgres.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(context, token));
        });
    }

    private async Task RunAsync(Func<OrderDbContext, CancellationToken, Task> action, string? target = null)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var scope = app.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        context.Database.SetCommandTimeout(30);
        await context.Database.OpenConnectionAsync(lifetime.Token);
        try
        {
            if (target is not null) await MigrateAsync(context, target, lifetime.Token);
            await action(context, lifetime.Token);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task MigrateAsync(OrderDbContext context, string target, CancellationToken token) =>
        context.GetService<IMigrator>().MigrateAsync(target, token);

    private static async Task AssertRefusedAsync(OrderDbContext context, string target, CancellationToken token)
    {
        var before = await SnapshotAsync(context, token);
        var lockTimeout = await ScalarAsync(context, "SHOW lock_timeout", token);
        var statementTimeout = await ScalarAsync(context, "SHOW statement_timeout", token);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, target, token));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(context, token));
        Assert.Equal(lockTimeout, await ScalarAsync(context, "SHOW lock_timeout", token));
        Assert.Equal(statementTimeout, await ScalarAsync(context, "SHOW statement_timeout", token));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal("True", await ScalarAsync(context, "SELECT to_regclass('pg_temp.\"__OrderTrackingSourceGuard\"') IS NULL", token));
    }

    private static Task SeedAsync(OrderDbContext context, CancellationToken token) => ExecuteAsync(context, """
        INSERT INTO public."Category" ("ID","Name") VALUES (1,'Tracking category');
        INSERT INTO public."Process" ("ID","CategoryID","Name") VALUES (1,1,'Tracking process');
        INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment","TrackingNumber")
            VALUES (1,1,1,0,false,false,false,'Target'),(2,1,2,0,false,false,false,'Retained');
        INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") VALUES (1,2,'retained','part.step');
        INSERT INTO public."FileFormat" ("ID","Name","Extension") VALUES (1,'Retained','.step');
        """, token);

    private static async Task SetTrackingAsync(OrderDbContext context, string value, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "UPDATE public.\"Order\" SET \"TrackingNumber\"=@value WHERE \"ID\"=1";
        command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = value, Size = -1 });
        Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
    }

    private static async Task ExecuteAsync(OrderDbContext context, string sql, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<string?> ScalarAsync(OrderDbContext context, string sql, CancellationToken token)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(token);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Snapshot(string Rows, string? Schema, string Migrations);

    private static async Task<Snapshot> SnapshotAsync(OrderDbContext context, CancellationToken token) => new(
        JsonSerializer.Serialize(new
        {
            Orders = await context.Orders.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Categories = await context.Categories.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Processes = await context.Processes.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Files = await context.Files.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Formats = await context.FileFormats.AsNoTracking().OrderBy(value => value.Id).ToArrayAsync(token),
            Intents = await context.DeletionIntents.AsNoTracking().OrderBy(value => value.OrderId).ToArrayAsync(token),
        }),
        await ScalarAsync(context, """
            SELECT jsonb_build_object(
                'columns',(SELECT jsonb_agg(row_to_json(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE c.table_schema='public' AND c.table_name IN ('Order','Category','Process','OrderFile','FileFormat','OrderDeletionIntent')),
                'constraints',(SELECT jsonb_agg(jsonb_build_array(c.conname,pg_get_constraintdef(c.oid),c.convalidated) ORDER BY c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid WHERE t.relnamespace='public'::regnamespace AND t.relname IN ('Order','Category','Process','OrderFile','FileFormat','OrderDeletionIntent')),
                'indexes',(SELECT jsonb_agg(jsonb_build_array(indexname,indexdef) ORDER BY indexname) FROM pg_indexes WHERE schemaname='public'),
                'triggers',(SELECT jsonb_agg(jsonb_build_array(t.tgname,pg_get_triggerdef(t.oid),t.tgenabled) ORDER BY t.tgname) FROM pg_trigger t WHERE t.tgrelid='public."Order"'::regclass AND NOT t.tgisinternal),
                'functions',(SELECT jsonb_agg(pg_get_functiondef(p.oid) ORDER BY p.proname) FROM pg_proc p WHERE p.pronamespace='public'::regnamespace AND p.proname IN ('NormalizeOrderTrackingSource','tracking_collision')))::text
            """, token),
        JsonSerializer.Serialize(await context.Database.GetAppliedMigrationsAsync(token)));

    private sealed class PreviousOrder
    {
        public int Id { get; set; }
        public int ProcessId { get; set; }
        public int Quantity { get; set; }
        public int Manufactured { get; set; }
        public bool AllowSocialMedia { get; set; }
        public bool AllowCancellation { get; set; }
        public bool AllowPayment { get; set; }
        public string? TrackingNumber { get; set; }
    }

    private sealed class PreviousTrackingContext(DbContextOptions<PreviousTrackingContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var order = builder.Entity<PreviousOrder>();
            order.ToTable("Order");
            order.HasKey(value => value.Id);
            order.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            order.Property(value => value.ProcessId).HasColumnName("ProcessID");
            order.Property(value => value.TrackingNumber).HasMaxLength(250).HasColumnType("character varying(250)");
        }
    }
}
