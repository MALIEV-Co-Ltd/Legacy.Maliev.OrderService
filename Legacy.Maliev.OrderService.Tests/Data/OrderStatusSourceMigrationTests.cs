using System.Diagnostics;
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

// Independent source rules: Name required only against NULL, UTF-16 length50; nullable Description length100.
// Forecast26: model1 + physical4 + literal4 + NULL1 + retained3 + cap1 + schema6 + cycle1 + Down4 + lock1.
public sealed class OrderStatusSourceMigrationTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    private const string Previous = "20260721030107_FixTimestampColumnType";
    private const string Target = "20261007120000_RequireOrderStatusSourceStrings";

    [Fact]
    public async Task ModelAndSnapshotRetainExactSourceStringsAndChecks()
    {
        await using var owned = await OpenAsync();
        var context = owned.Context;
        Assert.False(context.Database.HasPendingModelChanges());
        var snapshot = context.GetService<IMigrationsAssembly>().ModelSnapshot;
        Assert.NotNull(snapshot);
        foreach (var model in new[] { context.GetService<IDesignTimeModel>().Model, snapshot.Model })
        {
            var entity = model.FindEntityType(typeof(OrderStatus).FullName!);
            Assert.NotNull(entity);
            foreach (var (field, maximum, nullable) in new[] { ("Name", 50, false), ("Description", 100, true) })
            {
                var property = entity.FindProperty(field);
                Assert.NotNull(property);
                Assert.Equal(nullable, property.IsNullable);
                Assert.Equal(maximum, property.GetMaxLength());
                Assert.Equal("text", property.GetColumnType());
                var check = Assert.Single(entity.GetCheckConstraints(), value => value.Name == Check(field));
                Assert.Equal(LengthSql(field, maximum), check.Sql.Trim());
            }
        }
    }

    [Theory]
    [InlineData("Name", 50, false)]
    [InlineData("Name", 50, true)]
    [InlineData("Description", 100, false)]
    [InlineData("Description", 100, true)]
    public async Task PhysicalUtf16LimitsPreserveExactTrailingSpacesAndRejectOverflow(string field, int maximum, bool supplementary)
    {
        await using var owned = await OpenAsync();
        var context = owned.Context;
        await SeedAsync(context);
        var exact = supplementary ? string.Concat(Enumerable.Repeat("😀", (maximum - 2) / 2)) + "  " : new string('ก', maximum - 2) + "  ";
        Assert.Equal(maximum, exact.Length);
        await SetAsync(context, field, exact);
        Assert.Equal(exact, await ScalarAsync<string>(context, $"SELECT {Quote(field)} FROM public.\"OrderStatus\" WHERE \"ID\"=1"));
        var before = await SnapshotAsync(context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => SetAsync(context, field, exact + "ก"));
        Assert.Equal("23514", failure.SqlState);
        Assert.Equal(Check(field), failure.ConstraintName);
        Assert.Equal(before, await SnapshotAsync(context));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("  ", "")]
    [InlineData(" padded ", "  ")]
    [InlineData("ก", " padded description ")]
    public async Task NullOnlyRequirednessAllowsEmptyPaddingAndNullableDescription(string name, string? description)
    {
        await using var owned = await OpenAsync();
        var context = owned.Context;
        await SeedAsync(context);
        await SetAsync(context, "Name", name);
        await SetAsync(context, "Description", description);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var row = await context.Statuses.AsNoTracking().SingleAsync(value => value.Id == 1, lifetime.Token);
        Assert.Equal(name, row.Name);
        Assert.Equal(description, row.Description);
    }

    [Fact]
    public async Task PhysicalNullNameRefusesWithoutChangingAnyRetainedState()
    {
        await using var owned = await OpenAsync();
        await SeedAsync(owned.Context);
        var before = await SnapshotAsync(owned.Context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => SetAsync(owned.Context, "Name", null));
        Assert.Equal("23502", failure.SqlState);
        Assert.Equal("Name", failure.ColumnName);
        Assert.Equal(before, await SnapshotAsync(owned.Context));
    }

    [Theory]
    [InlineData("Name", "null")]
    [InlineData("Name", "supplementary")]
    [InlineData("Description", "overflow")]
    public async Task RetainedInvalidSourceRowsRefuseUpgradeBeforeAnySchemaOrHistoryChange(string field, string invalid)
    {
        await using var owned = await OpenAsync(Previous);
        var context = owned.Context;
        await SeedAsync(context);
        var value = invalid switch
        {
            "null" => null,
            "supplementary" => string.Concat(Enumerable.Repeat("😀", 26)),
            _ => new string('ก', 101),
        };
        await SetAsync(context, field, value);
        var before = await SnapshotAsync(context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, Target));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(context));
    }

    [Fact]
    public async Task RowAdmissionRejects10001ThenAccepts10000WithoutRewritingRows()
    {
        await using var owned = await OpenAsync(Previous);
        var context = owned.Context;
        await SeedAsync(context);
        await ExecuteAsync(context, "INSERT INTO public.\"OrderStatus\" (\"ID\",\"Name\",\"Description\",\"CreatedDate\",\"ModifiedDate\") SELECT g,\"Name\",\"Description\",\"CreatedDate\",\"ModifiedDate\" FROM public.\"OrderStatus\" CROSS JOIN generate_series(3,10001) g WHERE \"ID\"=1");
        Assert.Equal(10001, await ScalarAsync<int>(context, "SELECT count(*)::int FROM public.\"OrderStatus\""));
        var before = await SnapshotAsync(context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, Target));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(context));
        await ExecuteAsync(context, "DELETE FROM public.\"OrderStatus\" WHERE \"ID\"=10001");
        var admitted = await SnapshotAsync(context);
        await MigrateAsync(context, Target);
        Assert.Equal(admitted.Rows, (await SnapshotAsync(context)).Rows);
        Assert.Equal(10000, await ScalarAsync<int>(context, "SELECT count(*)::int FROM public.\"OrderStatus\""));
        await ExecuteAsync(context, "INSERT INTO public.\"OrderStatus\" (\"ID\",\"Name\",\"Description\") VALUES (10001,'Synthetic extra','Synthetic extra')");
        var overfull = await SnapshotAsync(context);
        var downgrade = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, Previous));
        Assert.Equal("P0001", downgrade.SqlState);
        Assert.Equal(overfull, await SnapshotAsync(context));
        await ExecuteAsync(context, "DELETE FROM public.\"OrderStatus\" WHERE \"ID\"=10001");
        await MigrateAsync(context, Previous);
        Assert.Equal(admitted, await SnapshotAsync(context));
    }

    [Theory]
    [InlineData("name-type")]
    [InlineData("description-type")]
    [InlineData("name-required")]
    [InlineData("description-required")]
    [InlineData("name-check")]
    [InlineData("description-check")]
    public async Task UnexpectedPreimageOrNamedCheckCollisionRefusesAtomicUpgrade(string mutation)
    {
        await using var owned = await OpenAsync(Previous);
        var context = owned.Context;
        await SeedAsync(context);
        var sql = mutation switch
        {
            "name-type" => "ALTER TABLE public.\"OrderStatus\" ALTER COLUMN \"Name\" TYPE text",
            "description-type" => "ALTER TABLE public.\"OrderStatus\" ALTER COLUMN \"Description\" TYPE varchar(100)",
            "name-required" => "ALTER TABLE public.\"OrderStatus\" ALTER COLUMN \"Name\" SET NOT NULL",
            "description-required" => "ALTER TABLE public.\"OrderStatus\" ALTER COLUMN \"Description\" SET NOT NULL",
            "name-check" => $"ALTER TABLE public.\"OrderStatus\" ADD CONSTRAINT {Quote(Check("Name"))} CHECK (true)",
            _ => $"ALTER TABLE public.\"OrderStatus\" ADD CONSTRAINT {Quote(Check("Description"))} CHECK (true)",
        };
        await ExecuteAsync(context, sql);
        var before = await SnapshotAsync(context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, Target));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(context));
    }

    [Fact]
    public async Task UpDownUpPreservesEntireStatusGraphAndRestoresExactPreimageMetadata()
    {
        await using var owned = await OpenAsync(Previous);
        var context = owned.Context;
        await SeedAsync(context);
        await SetAsync(context, "Name", string.Concat(Enumerable.Repeat("😀", 24)) + "  ");
        await SetAsync(context, "Description", string.Concat(Enumerable.Repeat("😀", 49)) + "  ");
        var prior = await SnapshotAsync(context);
        await MigrateAsync(context, Target);
        var installed = await SnapshotAsync(context);
        Assert.Equal(prior.Rows, installed.Rows);
        Assert.NotEqual(prior.Schema, installed.Schema);
        await MigrateAsync(context, Previous);
        Assert.Equal(prior, await SnapshotAsync(context));
        await MigrateAsync(context, Target);
        Assert.Equal(installed, await SnapshotAsync(context));
        Assert.Equal(0, await ScalarAsync<int>(context, "SELECT count(*)::int FROM pg_class WHERE relname='__OrderStatusSourceStringGuard' AND relnamespace=pg_my_temp_schema()"));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("Description", false)]
    [InlineData("Description", true)]
    public async Task MissingOrWrongOwnedCheckRefusesDowngradeAndRollsBackTempGuard(string field, bool wrongDefinition)
    {
        await using var owned = await OpenAsync();
        var context = owned.Context;
        await SeedAsync(context);
        await ExecuteAsync(context, $"ALTER TABLE public.\"OrderStatus\" DROP CONSTRAINT {Quote(Check(field))}");
        if (wrongDefinition) await ExecuteAsync(context, $"ALTER TABLE public.\"OrderStatus\" ADD CONSTRAINT {Quote(Check(field))} CHECK ({Quote(field)} IS NOT NULL)");
        var before = await SnapshotAsync(context);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(context, Previous));
        Assert.Equal("P0001", failure.SqlState);
        Assert.Equal(before, await SnapshotAsync(context));
        Assert.Equal(0, await ScalarAsync<int>(context, "SELECT count(*)::int FROM pg_class WHERE relname='__OrderStatusSourceStringGuard' AND relnamespace=pg_my_temp_schema()"));
    }

    [Fact]
    public async Task WriterLockDeadlineRefusesUpgradeAndReleasesMigrationTransaction()
    {
        await using var owned = await OpenAsync(Previous);
        var context = owned.Context;
        await SeedAsync(context);
        var before = await SnapshotAsync(context);
        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        // Observe one actual database lock deadline; production DI retries55P03 as transient.
        await using var singleAttempt = new OrderStatusDbContext(
            new DbContextOptionsBuilder<OrderStatusDbContext>()
                .UseNpgsql(context.Database.GetConnectionString()).Options);
        singleAttempt.Database.SetCommandTimeout(40);
        Assert.False(singleAttempt.Database.CreateExecutionStrategy().RetriesOnFailure);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var blocker = new NpgsqlConnection(context.Database.GetConnectionString());
        await blocker.OpenAsync(lifetime.Token);
        await using var transaction = await blocker.BeginTransactionAsync(lifetime.Token);
        try
        {
            await using var command = new NpgsqlCommand("LOCK TABLE public.\"OrderStatus\" IN ACCESS EXCLUSIVE MODE", blocker, transaction) { CommandTimeout = 40 };
            await command.ExecuteNonQueryAsync(lifetime.Token);
            var watch = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(singleAttempt, Target));
            watch.Stop();
            Assert.Equal("55P03", failure.SqlState);
            Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(40));
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await transaction.RollbackAsync(cleanup.Token);
        }
        Assert.Equal(before, await SnapshotAsync(context));
        await MigrateAsync(context, Target);
        Assert.Equal(before.Rows, (await SnapshotAsync(context)).Rows);
    }

    private async Task<OwnedScope> OpenAsync(string? target = null)
    {
        var app = await fixture.AppAsync(null);
        AsyncServiceScope? scope = null;
        try
        {
            scope = app.Services.CreateAsyncScope();
            var context = scope.Value.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
            var authority = new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
            var database = authority.Database;
            if (database is null || !database.StartsWith("owned_", StringComparison.Ordinal)
                || !Guid.TryParseExact(database[6..], "N", out _)
                || authority.Host is not ("localhost" or "127.0.0.1") || authority.Pooling)
                throw new InvalidOperationException("Require the fixture-owned isolated status database.");
            context.Database.SetCommandTimeout(40);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await context.Database.OpenConnectionAsync(lifetime.Token);
            if (target is not null) await MigrateAsync(context, target);
            return new OwnedScope(app, scope.Value, context);
        }
        catch
        {
            try { if (scope is { } opened) await opened.DisposeAsync(); }
            finally { await app.DisposeAsync(); }
            throw;
        }
    }

    private sealed class OwnedScope(WebApplicationFactory<Program> app, AsyncServiceScope scope, OrderStatusDbContext context) : IAsyncDisposable
    {
        public OrderStatusDbContext Context => context;
        public async ValueTask DisposeAsync()
        {
            try { await scope.DisposeAsync(); }
            finally { await app.DisposeAsync(); }
        }
    }

    private static Task SeedAsync(OrderStatusDbContext context) => ExecuteAsync(context, """
        INSERT INTO public."OrderStatus" ("ID","Name","Description","CreatedDate","ModifiedDate") VALUES
            (1,'Synthetic source','Synthetic description',TIMESTAMP '2026-10-07 00:00:00',TIMESTAMP '2026-10-07 00:00:00'),
            (2,'Synthetic target','Synthetic description',TIMESTAMP '2026-10-07 00:00:01',TIMESTAMP '2026-10-07 00:00:01');
        INSERT INTO public."OrderStatusHasPossibleStatus" ("ID","OrderStatusID","PossibleStatusID") VALUES (1,1,2);
        INSERT INTO public."OrderStatusHistory" ("ID","OrderID","OrderStatusID") VALUES (1,84,1);
        """);

    private static Task SetAsync(OrderStatusDbContext context, string field, string? value) => ExecuteAsync(context,
        $"UPDATE public.\"OrderStatus\" SET {Quote(field)}=@value WHERE \"ID\"=1",
        new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = 0 });

    private static async Task MigrateAsync(OrderStatusDbContext context, string target)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await context.GetService<IMigrator>().MigrateAsync(target, lifetime.Token);
    }

    private static async Task ExecuteAsync(OrderStatusDbContext context, string sql, params NpgsqlParameter[] parameters)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Database.GetDbConnection()) { CommandTimeout = 40 };
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(lifetime.Token);
    }

    private static async Task<T> ScalarAsync<T>(OrderStatusDbContext context, string sql, CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(45));
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)context.Database.GetDbConnection()) { CommandTimeout = 40 };
        return (T)(await command.ExecuteScalarAsync(lifetime.Token) ?? throw new InvalidOperationException("Owned scalar query returned no value."));
    }

    private sealed record Snapshot(string Rows, string Schema, string History);
    private static async Task<Snapshot> SnapshotAsync(OrderStatusDbContext context)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var rows = new List<string>();
        foreach (var table in new[] { "OrderStatus", "OrderStatusHasPossibleStatus", "OrderStatusHistory" })
            rows.Add(table + ":" + await ScalarAsync<string>(context, $"SELECT COALESCE(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text)::text,'[]') FROM public.{Quote(table)} r", lifetime.Token));
        var schema = await ScalarAsync<string>(context, """
            SELECT jsonb_build_object(
                'columns',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.table_name,c.ordinal_position) FROM information_schema.columns c WHERE table_schema='public' AND table_name IN ('OrderStatus','OrderStatusHasPossibleStatus','OrderStatusHistory')),
                'constraints',(SELECT jsonb_agg(jsonb_build_object('table',t.relname,'name',c.conname,'definition',pg_get_constraintdef(c.oid),'validated',c.convalidated,'local',c.conislocal,'inherit',c.coninhcount,'noinherit',c.connoinherit) ORDER BY t.relname,c.conname) FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='public' AND t.relname IN ('OrderStatus','OrderStatusHasPossibleStatus','OrderStatusHistory')),
                'indexes',(SELECT jsonb_agg(to_jsonb(i) ORDER BY tablename,indexname) FROM pg_indexes i WHERE schemaname='public' AND tablename IN ('OrderStatus','OrderStatusHasPossibleStatus','OrderStatusHistory'))
            )::text
            """, lifetime.Token);
        var history = await ScalarAsync<string>(context, "SELECT COALESCE(jsonb_agg(to_jsonb(h) ORDER BY \"MigrationId\")::text,'[]') FROM public.\"__EFMigrationsHistory\" h", lifetime.Token);
        return new Snapshot(string.Join("\n", rows), schema, history);
    }

    private static string Check(string field) => "CK_OrderStatus_" + field + "_SourceLength";
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string LengthSql(string field, int maximum) => $"char_length(\"{field}\") + char_length(regexp_replace(\"{field}\" COLLATE \"C\", U&'[\\0001-\\FFFF]', '', 'g')) <= {maximum}";
}
