using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Tests.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

public sealed class ProcessSourceNameMigrationTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const string Previous = "20261001051000_AddOrderDeletionIntent";
    private const string Target = "20261007140000_RequireProcessSourceName";

    [Fact]
    public async Task ModelAndSnapshotRetainRequiredNameAndIndependentUtf16Check()
    {
        await RunOwnedAsync(async owned =>
        {
            var context = owned.Context;
            Assert.False(context.Database.HasPendingModelChanges());
            var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
            Assert.NotNull(snapshot);
            foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
            {
                var entity = model.FindEntityType(typeof(Legacy.Maliev.OrderService.Domain.Process).FullName!);
                Assert.NotNull(entity);
                var name = entity.FindProperty("Name");
                Assert.NotNull(name);
                Assert.False(name.IsNullable);
                Assert.Equal(50, name.GetMaxLength());
                Assert.Equal("text", name.GetColumnType());
                Assert.Equal(LengthSql("Name", 50), Assert.Single(entity.GetCheckConstraints(), value => value.Name == Check("Name")).Sql.Trim());
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalUtf16BoundaryPreservesPaddingAndRefusesOverflow(bool supplementary)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            var exact = supplementary ? string.Concat(Enumerable.Repeat("😀", 24)) + "  " : new string('ก', 48) + "  ";
            Assert.Equal(50, exact.Length);
            await SetAsync(owned.Context, "Name", exact);
            Assert.Equal(exact, await ScalarAsync<string>(owned.Context, "SELECT \"Name\" FROM public.\"Process\" WHERE \"ID\"=1"));
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => SetAsync(owned.Context, "Name", exact + "ก"));
            Assert.Equal("23514", failure.SqlState);
            Assert.Equal(Check("Name"), failure.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
        });
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" ก padded ")]
    public async Task EmptyAndPaddedNamesSurvivePhysicalWrites(string name)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await SetAsync(owned.Context, "Name", name);
            Assert.Equal(name, await ScalarAsync<string>(owned.Context, "SELECT \"Name\" FROM public.\"Process\" WHERE \"ID\"=1"));
        });
    }

    [Fact]
    public async Task PhysicalNullNameRefusesWithoutChangingWholeGraph()
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => SetAsync(owned.Context, "Name", null));
            Assert.Equal("23502", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
        });
    }

    [Fact]
    public async Task RetainedSupplementaryOverflowRefusesUpgradeWithoutRewritingRows()
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            var invalid = string.Concat(Enumerable.Repeat("😀", 26));
            Assert.Equal(52, invalid.Length);
            Assert.Equal(26, invalid.EnumerateRunes().Count());
            await SetAsync(owned.Context, "Name", invalid);
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(owned.Context, Target));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
        }, Previous);
    }

    [Fact]
    public async Task RowAdmissionRefuses10001ThenAccepts10000WithoutRewritingRows()
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await ExecuteAsync(owned.Context, """
                INSERT INTO public."Process" ("ID","CategoryID","Name") SELECT value,1,'Synthetic cap row' FROM generate_series(2,10001) value;
                """);
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(owned.Context, Target));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
            await ExecuteAsync(owned.Context, "DELETE FROM public.\"Process\" WHERE \"ID\"=10001");
            var admitted = await SnapshotAsync(owned.Context);
            await MigrateAsync(owned.Context, Target);
            Assert.Equal(admitted.Rows, (await SnapshotAsync(owned.Context)).Rows);
        }, Previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedPreimageOrCheckCollisionRefusesAtomicUpgrade(bool collision)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await ExecuteAsync(owned.Context, collision
                ? "ALTER TABLE public.\"Process\" ADD CONSTRAINT \"CK_Process_Name_SourceLength\" CHECK (true)"
                : "ALTER TABLE public.\"Process\" ALTER COLUMN \"Name\" TYPE text");
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(owned.Context, Target));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
        }, Previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrWrongOwnedCheckRefusesDowngrade(bool wrong)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await ExecuteAsync(owned.Context, "ALTER TABLE public.\"Process\" DROP CONSTRAINT \"CK_Process_Name_SourceLength\"");
            if (wrong) await ExecuteAsync(owned.Context, "ALTER TABLE public.\"Process\" ADD CONSTRAINT \"CK_Process_Name_SourceLength\" CHECK (true)");
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(owned.Context, Previous));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
            Assert.True(await ScalarAsync<bool>(owned.Context, "SELECT to_regclass('pg_temp.\"__ProcessSourceNameGuard\"') IS NULL"));
        });
    }

    [Fact]
    public async Task UpDownUpPreservesWholeGraphForeignKeysIndexesAndSessionTimeouts()
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await SetAsync(owned.Context, "Name", string.Concat(Enumerable.Repeat("😀", 24)) + "  ");
            var before = await SnapshotAsync(owned.Context);
            var lockTimeout = await ScalarAsync<string>(owned.Context, "SHOW lock_timeout");
            var statementTimeout = await ScalarAsync<string>(owned.Context, "SHOW statement_timeout");
            await MigrateAsync(owned.Context, Target);
            Assert.Equal(before.Rows, (await SnapshotAsync(owned.Context)).Rows);
            await MigrateAsync(owned.Context, Previous);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await MigrateAsync(owned.Context, Target);
            Assert.Equal(before.Rows, (await SnapshotAsync(owned.Context)).Rows);
            Assert.Equal(lockTimeout, await ScalarAsync<string>(owned.Context, "SHOW lock_timeout"));
            Assert.Equal(statementTimeout, await ScalarAsync<string>(owned.Context, "SHOW statement_timeout"));
            Assert.Null(owned.Context.Database.CurrentTransaction);
            Assert.True(await ScalarAsync<bool>(owned.Context, "SELECT to_regclass('pg_temp.\"__ProcessSourceNameGuard\"') IS NULL"));
        }, Previous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidRetainedRowsRefuseDowngradeWithoutNarrowingOrRewriting(bool supplementary)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await ExecuteAsync(owned.Context, "ALTER TABLE public.\"Process\" DROP CONSTRAINT \"CK_Process_Name_SourceLength\"");
            var invalid = supplementary ? string.Concat(Enumerable.Repeat("😀", 26)) : new string('ก', 51);
            await SetAsync(owned.Context, "Name", invalid);
            await ExecuteAsync(owned.Context, "ALTER TABLE public.\"Process\" ADD CONSTRAINT \"CK_Process_Name_SourceLength\" CHECK (" + LengthSql("Name", 50) + ") NOT VALID");
            Assert.Equal(invalid, await ScalarAsync<string>(owned.Context, "SELECT \"Name\" FROM public.\"Process\" WHERE \"ID\"=1"));
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(owned.Context, Previous));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
        });
    }

    [Fact]
    public async Task ContestedWriterLockBoundsOneAttemptAndRestoresItsSession()
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            var before = await SnapshotAsync(owned.Context);
            Assert.True(owned.Context.Database.CreateExecutionStrategy().RetriesOnFailure);
            // One actual SQL lock deadline, not production retry/cancellation acceptance.
            OrderDbContext? singleAttempt = null;
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            NpgsqlConnection? blocker = null;
            NpgsqlTransaction? transaction = null;
            NpgsqlCommand? command = null;
            await ProcessSourceCleanup.RunAsync(async () =>
            {
                singleAttempt = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>()
                    .UseNpgsql(owned.Context.Database.GetConnectionString()).Options);
                singleAttempt.Database.SetCommandTimeout(40);
                Assert.False(singleAttempt.Database.CreateExecutionStrategy().RetriesOnFailure);
                await singleAttempt.Database.OpenConnectionAsync(lifetime.Token);
                var session = await FailureSessionAsync(singleAttempt);
                blocker = new NpgsqlConnection(owned.Context.Database.GetConnectionString());
                await blocker.OpenAsync(lifetime.Token);
                transaction = await blocker.BeginTransactionAsync(lifetime.Token);
                command = new NpgsqlCommand("LOCK TABLE public.\"Process\" IN ACCESS EXCLUSIVE MODE", blocker, transaction) { CommandTimeout = 40 };
                await command.ExecuteNonQueryAsync(lifetime.Token);
                var watch = Stopwatch.StartNew();
                var wrapper = await Assert.ThrowsAsync<InvalidOperationException>(() => MigrateAsync(singleAttempt, Target));
                var failure = Assert.IsType<PostgresException>(wrapper.InnerException);
                watch.Stop();
                Assert.Equal("55P03", failure.SqlState);
                Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(40));
                await AssertFailureSessionRestoredAsync(singleAttempt, session);
            }, async () =>
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                if (transaction is not null) await transaction.RollbackAsync(cleanup.Token);
            },
                () => command?.DisposeAsync() ?? ValueTask.CompletedTask,
                () => transaction?.DisposeAsync() ?? ValueTask.CompletedTask,
                () => blocker?.DisposeAsync() ?? ValueTask.CompletedTask,
                () => singleAttempt?.DisposeAsync() ?? ValueTask.CompletedTask);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await MigrateAsync(owned.Context, Target);
            Assert.Equal(before.Rows, (await SnapshotAsync(owned.Context)).Rows);
        }, Previous);
    }

    [Theory]
    [InlineData("Category", "Name")]
    [InlineData("FileFormat", "Name")]
    [InlineData("FileFormat", "Extension")]
    public async Task NullableCatalogUpDownUpPreservesWholeGraphAndUtf16Padding(string table, string field)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            await ExecuteAsync(owned.Context, """
                INSERT INTO public."Category" ("ID","Name") VALUES (2,NULL),(3,'');
                INSERT INTO public."FileFormat" ("ID","Name","Extension") VALUES (2,NULL,NULL),(3,'','');
                """);
            var exact = string.Concat(Enumerable.Repeat("😀", 24)) + "  ";
            Assert.Equal(50, exact.Length);
            await SetNullableCatalogAsync(owned.Context, table, field, exact);
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            await MigrateAsync(owned.Context, "20261018100000_EnforceNullableCatalogSourceStrings");
            Assert.Equal(before.Rows, (await SnapshotAsync(owned.Context)).Rows);
            Assert.False(owned.Context.Database.HasPendingModelChanges());
            Assert.Equal(3L, await ScalarAsync<long>(owned.Context, """
                SELECT count(*) FROM pg_constraint
                WHERE conrelid IN ('public."Category"'::regclass,'public."FileFormat"'::regclass)
                AND conname IN ('CK_Category_Name_SourceLength','CK_FileFormat_Name_SourceLength','CK_FileFormat_Extension_SourceLength')
                AND contype='c' AND convalidated AND conislocal AND coninhcount=0 AND NOT connoinherit
                """));
            var upgraded = await SnapshotAsync(owned.Context);
            await MigrateAsync(owned.Context, Target);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await MigrateAsync(owned.Context, "20261018100000_EnforceNullableCatalogSourceStrings");
            Assert.Equal(upgraded, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
            Assert.True(await ScalarAsync<bool>(owned.Context, "SELECT to_regclass('pg_temp.\"__NullableCatalogSourceStringsGuard\"') IS NULL"));
        }, Target);
    }

    [Theory]
    [InlineData("Category", "Name")]
    [InlineData("FileFormat", "Name")]
    [InlineData("FileFormat", "Extension")]
    public async Task NullableCatalogRetainedSupplementaryOverflowRefusesAtomicUpgrade(string table, string field)
    {
        await RunOwnedAsync(async owned =>
        {
            await SeedAsync(owned.Context);
            var invalid = string.Concat(Enumerable.Repeat("😀", 26));
            Assert.Equal(52, invalid.Length);
            Assert.Equal(26, invalid.EnumerateRunes().Count());
            await SetNullableCatalogAsync(owned.Context, table, field, invalid);
            var before = await SnapshotAsync(owned.Context);
            var session = await FailureSessionAsync(owned.Context);
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                MigrateAsync(owned.Context, "20261018100000_EnforceNullableCatalogSourceStrings"));
            Assert.Equal("P0001", failure.SqlState);
            Assert.Equal(before, await SnapshotAsync(owned.Context));
            await AssertFailureSessionRestoredAsync(owned.Context, session);
            Assert.True(await ScalarAsync<bool>(owned.Context, "SELECT to_regclass('pg_temp.\"__NullableCatalogSourceStringsGuard\"') IS NULL"));
        }, Target);
    }

    private static Task SetNullableCatalogAsync(OrderDbContext context, string table, string field, string? value) =>
        ExecuteAsync(context, (table, field) switch
        {
            ("Category", "Name") => "UPDATE public.\"Category\" SET \"Name\"=@value WHERE \"ID\"=1",
            ("FileFormat", "Name") => "UPDATE public.\"FileFormat\" SET \"Name\"=@value WHERE \"ID\"=1",
            ("FileFormat", "Extension") => "UPDATE public.\"FileFormat\" SET \"Extension\"=@value WHERE \"ID\"=1",
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        }, new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = -1 });

    private sealed record FailureSession(string LockTimeout, string StatementTimeout);
    private static async Task<FailureSession> FailureSessionAsync(OrderDbContext context)
    {
        Assert.Null(context.Database.CurrentTransaction);
        return new FailureSession(await ScalarAsync<string>(context, "SHOW lock_timeout"),
            await ScalarAsync<string>(context, "SHOW statement_timeout"));
    }

    private static async Task AssertFailureSessionRestoredAsync(OrderDbContext context, FailureSession expected)
    {
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(expected.LockTimeout, await ScalarAsync<string>(context, "SHOW lock_timeout"));
        Assert.Equal(expected.StatementTimeout, await ScalarAsync<string>(context, "SHOW statement_timeout"));
        Assert.True(await ScalarAsync<bool>(context, "SELECT to_regclass('pg_temp.\"__ProcessSourceNameGuard\"') IS NULL"));
    }

    private async Task<OwnedScope> OpenAsync(string? target = null)
    {
        var app = await fixture.AppAsync(null);
        AsyncServiceScope? scope = null;
        try
        {
            scope = app.Services.CreateAsyncScope();
            var context = scope.Value.ServiceProvider.GetRequiredService<OrderDbContext>();
            var authority = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
            var database = authority.Database;
            if (database is null || !database.StartsWith("owned_", StringComparison.Ordinal)
                || !Guid.TryParseExact(database[6..], "N", out _)
                || authority.Host is not ("localhost" or "127.0.0.1") || authority.Pooling)
                throw new InvalidOperationException("Require the fixture-owned isolated order database.");
            context.Database.SetCommandTimeout(40);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await context.Database.OpenConnectionAsync(lifetime.Token);
            if (target is not null) await MigrateAsync(context, target);
            return new OwnedScope(app, scope.Value, context);
        }
        catch (Exception primary)
        {
            await ProcessSourceCleanup.RunAsync(() => Task.FromException(primary),
                () => scope is { } opened ? opened.DisposeAsync() : ValueTask.CompletedTask,
                app.DisposeAsync);
            throw;
        }
    }

    private sealed class OwnedScope(WebApplicationFactory<Program> app, AsyncServiceScope scope, OrderDbContext context) : IAsyncDisposable
    {
        public OrderDbContext Context => context;
        public ValueTask DisposeAsync() => new(ProcessSourceCleanup.RunAsync(
            () => Task.CompletedTask, scope.DisposeAsync, app.DisposeAsync));
    }

    private async Task RunOwnedAsync(Func<OwnedScope, Task> body, string? target = null)
    {
        var owned = await OpenAsync(target);
        await ProcessSourceCleanup.RunAsync(() => body(owned), owned.DisposeAsync);
    }

    private static Task SeedAsync(OrderDbContext context) => ExecuteAsync(context, """
        INSERT INTO public."Category" ("ID","Name") VALUES (1,'Synthetic category');
        INSERT INTO public."FileFormat" ("ID","Name","Extension") VALUES (1,'Synthetic format','.step');
        INSERT INTO public."Process" ("ID","CategoryID","Name","CreatedDate","ModifiedDate") VALUES
            (1,1,'Synthetic process',TIMESTAMP '2026-10-07 00:00:00',TIMESTAMP '2026-10-07 00:00:00');
        INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment") VALUES (1,1,3,1,false,true,false);
        INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") VALUES (1,1,'synthetic-bucket','synthetic.step');
        """);

    private static Task SetAsync(OrderDbContext context, string field, string? value) => ExecuteAsync(context,
        $"UPDATE public.\"Process\" SET {Quote(field)}=@value WHERE \"ID\"=1",
        new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = 0 });

    private static async Task MigrateAsync(OrderDbContext context, string target)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await context.GetService<IMigrator>().MigrateAsync(target, lifetime.Token);
    }

    private static async Task ExecuteAsync(OrderDbContext context, string sql, params NpgsqlParameter[] parameters)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Database.GetDbConnection()) { CommandTimeout = 40 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(lifetime.Token);
    }

    private static async Task<T> ScalarAsync<T>(OrderDbContext context, string sql, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(45));
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Database.GetDbConnection()) { CommandTimeout = 40 };
        return (T)(await command.ExecuteScalarAsync(lifetime.Token) ?? throw new InvalidOperationException("Owned scalar query returned no value."));
    }

    private sealed record Snapshot(string Rows, string Schema, string History);
    private static async Task<Snapshot> SnapshotAsync(OrderDbContext context)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var rows = new List<string>();
        foreach (var table in new[] { "Category", "FileFormat", "Process", "Order", "OrderFile", "OrderDeletionIntent" })
            rows.Add(table + ":" + await ScalarAsync<string>(context, $"SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text)::text,'[]') FROM public.{Quote(table)} r", lifetime.Token));
        var schema = await ScalarAsync<string>(context, """
            SELECT jsonb_build_object(
                'columns',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE table_schema='public' AND table_name IN ('Category','FileFormat','Process','Order','OrderFile','OrderDeletionIntent')),
                'constraints',(SELECT jsonb_agg(jsonb_build_object('table',t.relname,'name',c.conname,'definition',pg_get_constraintdef(c.oid),'validated',c.convalidated,'local',c.conislocal,'inherit',c.coninhcount,'noinherit',c.connoinherit) ORDER BY t.relname,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='public' AND t.relname IN ('Category','FileFormat','Process','Order','OrderFile','OrderDeletionIntent')),
                'indexes',(SELECT jsonb_agg(to_jsonb(i) ORDER BY tablename,indexname) FROM pg_indexes i WHERE schemaname='public' AND tablename IN ('Category','FileFormat','Process','Order','OrderFile','OrderDeletionIntent'))
            )::text
            """, lifetime.Token);
        var history = await ScalarAsync<string>(context, "SELECT COALESCE(jsonb_agg(to_jsonb(h) ORDER BY \"MigrationId\")::text,'[]') FROM public.\"__EFMigrationsHistory\" h", lifetime.Token);
        return new Snapshot(string.Join("\n", rows), schema, history);
    }

    private static string Check(string field) => "CK_Process_" + field + "_SourceLength";
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string LengthSql(string field, int maximum) => $"char_length(\"{field}\") + char_length(regexp_replace(\"{field}\" COLLATE \"C\", U&'[\\0001-\\FFFF]', '', 'g')) <= {maximum}";
}

internal static class ProcessSourceCleanup
{
    internal static async Task RunAsync(Func<Task> body, params Func<ValueTask>[] cleanup)
    {
        var failures = new List<Exception>();
        try { await body(); }
        catch (Exception primary) { failures.Add(primary); }
        foreach (var action in cleanup)
        {
            try { await action(); }
            catch (Exception failure) { failures.Add(failure); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Process test operation and cleanup failed.", failures);
    }
}

public sealed class ProcessSourceCleanupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupRefusalDisposesAcquiredResourceAndPreservesPrimary(bool disposalFails)
    {
        var calls = new List<string>();
        var primary = new InvalidOperationException("Synthetic setup refusal");
        var cleanupFailure = new InvalidOperationException("Synthetic disposal failure");
        Func<ValueTask>? dispose = null;
        var failure = await Record.ExceptionAsync(() => ProcessSourceCleanup.RunAsync(() =>
        {
            calls.Add("acquire");
            dispose = () => { calls.Add("dispose"); return disposalFails ? ValueTask.FromException(cleanupFailure) : ValueTask.CompletedTask; };
            calls.Add("configure");
            return Task.FromException(primary);
        }, () => dispose?.Invoke() ?? ValueTask.CompletedTask));
        Assert.Equal(new[] { "acquire", "configure", "dispose" }, calls);
        if (!disposalFails) Assert.Same(primary, failure);
        else
        {
            var combined = Assert.IsType<AggregateException>(failure);
            Assert.Equal(2, combined.InnerExceptions.Count);
            Assert.Same(primary, combined.InnerExceptions[0]);
            Assert.Same(cleanupFailure, combined.InnerExceptions[1]);
        }
    }

    [Theory]
    [InlineData("scope", 0)]
    [InlineData("scope", 1)]
    [InlineData("scope", 2)]
    [InlineData("scope", 3)]
    [InlineData("scope", 4)]
    [InlineData("scope", 5)]
    [InlineData("scope", 6)]
    [InlineData("scope", 7)]
    [InlineData("rollback", 0)]
    [InlineData("rollback", 1)]
    [InlineData("rollback", 2)]
    [InlineData("rollback", 3)]
    [InlineData("rollback", 4)]
    [InlineData("rollback", 5)]
    [InlineData("rollback", 6)]
    [InlineData("rollback", 7)]
    public async Task PrimaryAndBothCleanupFailuresRetainIdentityAndOrder(string firstCleanup, int failures)
    {
        var calls = new List<string>();
        var primary = new InvalidOperationException("Synthetic primary");
        var first = new InvalidOperationException("Synthetic first cleanup");
        var second = new InvalidOperationException("Synthetic second cleanup");
        var expected = new List<Exception>();
        if ((failures & 1) != 0) expected.Add(primary);
        if ((failures & 2) != 0) expected.Add(first);
        if ((failures & 4) != 0) expected.Add(second);
        var finalCleanup = firstCleanup == "scope" ? "app" : "transaction";
        var failure = await Record.ExceptionAsync(() => ProcessSourceCleanup.RunAsync(
            () => { calls.Add("primary"); return (failures & 1) != 0 ? Task.FromException(primary) : Task.CompletedTask; },
            () => { calls.Add(firstCleanup); return (failures & 2) != 0 ? ValueTask.FromException(first) : ValueTask.CompletedTask; },
            () => { calls.Add(finalCleanup); return (failures & 4) != 0 ? ValueTask.FromException(second) : ValueTask.CompletedTask; }));
        Assert.Equal(new[] { "primary", firstCleanup, finalCleanup }, calls);
        if (expected.Count == 0) Assert.Null(failure);
        else if (expected.Count == 1) Assert.Same(expected[0], failure);
        else
        {
            var combined = Assert.IsType<AggregateException>(failure);
            Assert.Equal(expected.Count, combined.InnerExceptions.Count);
            for (var index = 0; index < expected.Count; index++) Assert.Same(expected[index], combined.InnerExceptions[index]);
        }
    }
}
