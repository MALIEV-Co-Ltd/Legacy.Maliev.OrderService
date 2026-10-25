using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

// DataMigration269/270 qualified the proposed-head oracle; require exact-main acceptance before this bundle merges.
// 5fac706a7983a6d359b39acbd670e6800afe020e (parents[]), OrderContext.cs130b92275a21a2ff9b5b6ac3d1d92f0e1074a001;
// 72eb9f1949176392141951d35e6e06f7c30af4c2 (parent023aa2f143fa5a8f557d72c1302b9add64792d6a), context c95270b54fcb6a82cf50e920bea5a0d020ed546f;
// 2aab25eb07894fc0267b03b85bad96490219d2fa (parent56014f2efc7c7f24b777c51d17a454e023d12bdd), context79d9f74582553559cdbb41417d7be4e1113db6d1.
// Current d23065d499333ece5ef73013b085a8aaa062b163 (parent449d87ea9d1d6ab40f1ec444353ab6aba80a5a76):
// Maliev.OrderService.Data/Database/OrderContext/OrderContext.cs79d9f74582553559cdbb41417d7be4e1113db6d1,
// OrderFile.cs f6fa00f63b591e20b622bcd8dbb1e287f0456e3d: required Bucket50, required unbounded ObjectName, no string defaults.
// Maliev.OrderService.Api/Controllers/FilesController.cs3621dbe000faa67a60fda392f1ec80c56f50f255 is a separate HTTP facet.
// Preserve accepted87/88,70 and108 HTTP/repository guards. These are PostgreSQL metadata regressions,
// not original SQL/EF8/MVC/deployed-session proof; no GCS, Document, event or financial operation is exercised.
public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    private static string BucketObjectName => "  drawings/" + new string('ก', 2048)
        + string.Concat(Enumerable.Repeat("😀", 2048)) + ".step  ";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BucketPhysicalPaddingKeepsLiteralParametersAndUnboundedCompanion(bool update, bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedBucketPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var nextId = 20;
        foreach (var input in Accepted(50).OfType<string>())
        {
            var id = update ? 2 : nextId++;
            var before = await BucketSnapshotAsync(app, token, id);
            var others = update ? await BucketOtherFieldsAsync(db, id, token) : null;
            Assert.Equal(input, await WriteBucketPaddingAsync(db, update, previousEf, id, input, BucketObjectName, false, token));
            var row = await db.Files.AsNoTracking().SingleAsync(value => value.Id == id, token);
            Assert.Equal(input.Length > 50 ? input[..50] : input, row.Bucket);
            Assert.Equal(BucketObjectName, row.ObjectName);
            Assert.Equal(6162, row.ObjectName.Length);
            Assert.Equal(1, row.OrderId);
            Assert.NotNull(row.CreatedDate);
            Assert.NotNull(row.ModifiedDate);
            if (update) Assert.Equal(others, await BucketOtherFieldsAsync(db, id, token));
            Assert.Equal(before, await BucketSnapshotAsync(app, token, id));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BucketNonPaddingOverflowStillRefusesAndRetainsEveryRow(bool update, bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedBucketPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        foreach (var input in Refused(50))
        {
            var before = await BucketSnapshotAsync(app, token);
            var error = await BucketFailureAsync(db, update, previousEf, update ? 2 : 20, input, BucketObjectName, false, token);
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("CK_OrderFile_Bucket_SourceLength", error.ConstraintName);
            Assert.Equal(before, await BucketSnapshotAsync(app, token));
            Assert.Null(db.Database.CurrentTransaction);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BucketHttpAndRepositoryKeepStrictOverflowAndMissingLifetimeOrder(bool update)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedBucketPaddingAsync(app, token);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.FilesWrite));
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var before = await BucketSnapshotAsync(app, token);
        foreach (var input in Accepted(50).OfType<string>().Where(value => value.Length > 50).Concat(Refused(50)))
        {
            using var response = update
                ? await client.PutAsJsonAsync("/orders/files/2", new UpsertOrderFileRequest(1, input, "control.step"), token)
                : await client.PostAsync($"/orders/1/files?bucket={Uri.EscapeDataString(input)}&objectName={Uri.EscapeDataString("control.step")}", null, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            if (update)
            {
                await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateFileAsync(2, new UpsertOrderFileRequest(1, input, "control.step"), token));
                Assert.False(await service.UpdateFileAsync(int.MaxValue, new UpsertOrderFileRequest(1, input, "control.step"), token));
            }
            else
            {
                await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFileAsync(1, input, "control.step", token));
                Assert.Null(await service.CreateFileAsync(int.MaxValue, input, "control.step", token));
            }
            Assert.Equal(before, await BucketSnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BucketAndObjectNameRequiredNullFailuresRetainSeedAndHaveNoDefaults(bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedBucketPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        foreach (var update in new[] { false, true })
        {
            foreach (var objectNull in new[] { false, true })
            {
                var before = await BucketSnapshotAsync(app, token);
                var error = await BucketFailureAsync(db, update, previousEf, update ? 2 : 20,
                    objectNull ? "before" : null, objectNull ? null : BucketObjectName, objectNull, token);
                Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
                Assert.Equal("OrderFile", error.TableName);
                Assert.Equal(objectNull ? "ObjectName" : "Bucket", error.ColumnName);
                Assert.Equal(before, await BucketSnapshotAsync(app, token));
                Assert.Null(db.Database.CurrentTransaction);
            }
        }
        foreach (var column in new[] { "Bucket", "ObjectName" })
        {
            var before = await BucketSnapshotAsync(app, token);
            var columns = column == "Bucket" ? "\"ObjectName\"" : "\"Bucket\"";
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
                $"INSERT INTO public.\"OrderFile\" (\"ID\",\"OrderID\",{columns}) VALUES (30,1,'control')", token));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
            Assert.Equal(column, error.ColumnName);
            Assert.Equal(before, await BucketSnapshotAsync(app, token));
        }
    }

    private static async Task SeedBucketPaddingAsync(WebApplicationFactory<Program> app, CancellationToken token)
    {
        await SeedCatalogPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.Database.SetCommandTimeout(30);
        var entity = db.Model.FindEntityType(typeof(Legacy.Maliev.OrderService.Domain.OrderFile));
        Assert.NotNull(entity);
        foreach (var name in new[] { "Bucket", "ObjectName" })
        {
            var property = entity.FindProperty(name);
            Assert.NotNull(property);
            Assert.False(property.IsNullable);
            Assert.Equal(name == "Bucket" ? 50 : (int?)null, property.GetMaxLength());
            Assert.Equal("text", property.GetColumnType());
            Assert.Null(property.GetDefaultValue());
            Assert.Null(property.GetDefaultValueSql());
        }
        var designEntity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Legacy.Maliev.OrderService.Domain.OrderFile));
        Assert.NotNull(designEntity);
        Assert.Equal("""char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50""",
            Assert.Single(designEntity.GetCheckConstraints(), constraint => constraint.Name == "CK_OrderFile_Bucket_SourceLength").Sql.Trim());
        Assert.Equal("True", await ProcessStatusScalarAsync(db, """
            SELECT (SELECT count(*)=2 FROM pg_attribute a
                WHERE a.attrelid='public."OrderFile"'::regclass AND a.attname IN ('Bucket','ObjectName')
                    AND a.attnotnull AND a.atttypid='text'::regtype AND a.atttypmod=-1 AND NOT a.attisdropped
                    AND NOT EXISTS (SELECT 1 FROM pg_attrdef d WHERE d.adrelid=a.attrelid AND d.adnum=a.attnum))
                AND EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conrelid='public."OrderFile"'::regclass
                    AND c.conname='CK_OrderFile_Bucket_SourceLength' AND c.contype='c' AND c.convalidated
                    AND c.conislocal AND c.coninhcount=0 AND NOT c.connoinherit
                    AND c.conkey=ARRAY[(SELECT attnum FROM pg_attribute
                        WHERE attrelid='public."OrderFile"'::regclass AND attname='Bucket')]::smallint[])
            """, token));
        Assert.Equal(6162, BucketObjectName.Length);
        // UPDATE observes ObjectName only on this seed INSERT; later Bucket UPDATE must not send it.
        Assert.Equal("before", await WriteBucketPaddingAsync(db, false, true, 2, "before", BucketObjectName, false, token));
    }

    private static async Task<string> BucketSnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, int excludedId = 0)
    {
        var parts = (await CatalogSnapshotAsync(app, token)).Split('\n');
        Assert.Equal(2, parts.Length);
        using var orders = JsonDocument.Parse(parts[0]);
        using var statuses = JsonDocument.Parse(parts[1]);
        return JsonSerializer.Serialize(new[] { orders.RootElement, statuses.RootElement }.Select(document =>
            document.EnumerateObject().ToDictionary(property => property.Name, property => property.Name == "files"
                ? JsonSerializer.SerializeToElement(property.Value.EnumerateArray().Where(row => row.GetProperty("ID").GetInt32() != excludedId).ToArray())
                : property.Value)));
    }

    private static Task<string?> BucketOtherFieldsAsync(OrderDbContext db, int id, CancellationToken token) =>
        ProcessStatusScalarAsync(db, $"SELECT (to_jsonb(t)-'Bucket')::text FROM public.\"OrderFile\" t WHERE t.\"ID\"={id}", token);

    private static async Task<PostgresException> BucketFailureAsync(OrderDbContext db, bool update, bool previousEf, int id, string? bucket, string? objectName, bool objectOnly, CancellationToken token)
    {
        if (!previousEf) return await Assert.ThrowsAsync<PostgresException>(() => WriteBucketPaddingAsync(db, update, false, id, bucket, objectName, objectOnly, token));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => WriteBucketPaddingAsync(db, update, true, id, bucket, objectName, objectOnly, token));
        return Assert.IsType<PostgresException>(error.InnerException);
    }

    private static async Task<string?> WriteBucketPaddingAsync(OrderDbContext db, bool update, bool previousEf, int id, string? bucket, string? objectName, bool objectOnly, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            var capture = new BucketParameterCapture(update, bucket, objectName, objectOnly);
            if (previousEf)
            {
                await using var previous = new BucketPreviousContext(new DbContextOptionsBuilder<BucketPreviousContext>()
                    .UseNpgsql(db.Database.GetDbConnection(), contextOwnsConnection: false).AddInterceptors(capture).Options);
                previous.Database.SetCommandTimeout(30);
                var row = new BucketPreviousFile { Id = id, OrderId = 1, Bucket = bucket, ObjectName = objectName };
                if (update)
                {
                    previous.Attach(row);
                    previous.Entry(row).Property(objectOnly ? "ObjectName" : "Bucket").IsModified = true;
                }
                else previous.Add(row);
                try { await previous.SaveChangesAsync(token); }
                catch (DbUpdateException) { Assert.Equal(1, capture.ValidatedCommands); throw; }
                Assert.Equal(1, capture.ValidatedCommands);
                Assert.Equal(objectName, row.ObjectName);
                return row.Bucket;
            }
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = update
                ? $"UPDATE public.\"OrderFile\" SET \"{(objectOnly ? "ObjectName" : "Bucket")}\"=@value WHERE \"ID\"=@id"
                : "INSERT INTO public.\"OrderFile\" (\"ID\",\"OrderID\",\"Bucket\",\"ObjectName\") VALUES (@id,1,@value,@objectName)";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)(update && objectOnly ? objectName : bucket) ?? DBNull.Value, Size = -1 });
            if (!update) command.Parameters.Add(new NpgsqlParameter("objectName", NpgsqlDbType.Text) { Value = (object?)objectName ?? DBNull.Value, Size = -1 });
            capture.Validate(command);
            Assert.Equal(1, capture.ValidatedCommands);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
            return bucket;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private sealed class BucketParameterCapture(bool update, string? bucket, string? objectName, bool objectOnly) : DbCommandInterceptor
    {
        public int ValidatedCommands { get; private set; }

        public void Validate(DbCommand command)
        {
            Assert.InRange(command.CommandText.Length, 1, 16_384);
            var target = "(?:\"?public\"?\\.)?\"OrderFile\"";
            var columns = new Dictionary<string, string>(StringComparer.Ordinal);
            if (update)
            {
                var match = Regex.Match(command.CommandText, "UPDATE\\s+" + target + "\\s+SET\\s+(?<set>.*?)\\s+WHERE\\s+",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Assert.True(match.Success);
                var assignment = Assert.Single(match.Groups["set"].Value.Split(','));
                var pair = assignment.Split('=');
                Assert.Equal(2, pair.Length);
                columns.Add(pair[0].Trim().Trim('"'), pair[1].Trim());
                Assert.Equal(objectOnly ? "ObjectName" : "Bucket", Assert.Single(columns.Keys));
            }
            else
            {
                var match = Regex.Match(command.CommandText, "INSERT\\s+INTO\\s+" + target + "\\s*\\((?<columns>[^)]*)\\)\\s*VALUES\\s*\\((?<values>[^)]*)\\)",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Assert.True(match.Success);
                var names = match.Groups["columns"].Value.Split(',').Select(value => value.Trim().Trim('"')).ToArray();
                var values = match.Groups["values"].Value.Split(',').Select(value => value.Trim()).ToArray();
                Assert.Equal(names.Length, values.Length);
                for (var index = 0; index < names.Length; index++) columns.Add(names[index], values[index]);
            }
            foreach (var field in update ? new[] { objectOnly ? "ObjectName" : "Bucket" } : new[] { "Bucket", "ObjectName" })
            {
                var selected = columns[field];
                var parameter = Assert.Single(command.Parameters.Cast<NpgsqlParameter>(), value => value.ParameterName.TrimStart('@') == selected.TrimStart('@'));
                var expected = field == "Bucket" ? bucket : objectName;
                Assert.Equal(NpgsqlDbType.Text, parameter.NpgsqlDbType);
                Assert.True(parameter.Size <= 0 || parameter.Size >= (expected?.Length ?? 0), "The selected parameter must retain the full input length.");
                Assert.Equal(expected, parameter.Value is null or DBNull ? null : Assert.IsType<string>(parameter.Value));
            }
            ValidatedCommands++;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Validate(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Validate(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BucketPreviousFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string? Bucket { get; set; }
        public string? ObjectName { get; set; }
    }

    private sealed class BucketPreviousContext(DbContextOptions<BucketPreviousContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var row = builder.Entity<BucketPreviousFile>();
            row.ToTable("OrderFile");
            row.HasKey(value => value.Id);
            row.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            row.Property(value => value.OrderId).HasColumnName("OrderID");
            row.Property(value => value.Bucket).IsRequired().HasMaxLength(50).HasColumnType("text");
            row.Property(value => value.ObjectName).IsRequired().HasColumnType("text");
        }
    }
}
