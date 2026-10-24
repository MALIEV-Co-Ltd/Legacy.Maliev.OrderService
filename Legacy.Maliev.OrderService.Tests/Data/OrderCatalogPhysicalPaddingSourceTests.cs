using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

// Source lineage: 5fac706a7983a6d359b39acbd670e6800afe020e (root, parents[]),
// 72eb9f1949176392141951d35e6e06f7c30af4c2 (parent023aa2f143fa5a8f557d72c1302b9add64792d6a),
// 2aab25eb07894fc0267b03b85bad96490219d2fa (parent56014f2efc7c7f24b777c51d17a454e023d12bdd).
// Maliev.OrderService.Data/Database/OrderContext/OrderContext.cs current blob79d9f74582553559cdbb41417d7be4e1113db6d1
// maps nullable Category.Name and FileFormat.Name/Extension to50 UTF16 units without string defaults.
// Category.cs bloba15c2cab37d63f244851279482c52e49d3ca556c; FileFormat.cs blob9001b8fbfd4626b5e5469a1558c0d1f4f094ba9d.
// DataMigration264/265's controlled current EF8.0.28 observations qualify only physical padding;
// initial EF8.0.3/deployed sessions remain unproved. Accepted Order85's HTTP/direct-repository guards stay strict.
public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    [Theory]
    [InlineData("Category", "Name", false, false)]
    [InlineData("Category", "Name", true, false)]
    [InlineData("FileFormat", "Name", false, false)]
    [InlineData("FileFormat", "Name", true, false)]
    [InlineData("FileFormat", "Extension", false, false)]
    [InlineData("FileFormat", "Extension", true, false)]
    [InlineData("Category", "Name", false, true)]
    [InlineData("Category", "Name", true, true)]
    [InlineData("FileFormat", "Name", false, true)]
    [InlineData("FileFormat", "Name", true, true)]
    [InlineData("FileFormat", "Extension", false, true)]
    [InlineData("FileFormat", "Extension", true, true)]
    public async Task CatalogPhysicalAndPreviousEfWritesNormalizeOnlyQualifiedExcessPadding(string table, string field, bool update, bool previousEf)
    {
        ValidateCatalogField(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedCatalogPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var nextId = 20;
        foreach (var input in Accepted(50))
        {
            var id = update ? 1 : nextId++;
            var before = await CatalogSnapshotAsync(app, token, table, id);
            var others = update ? await CatalogRowAsync(db, table, id, field, token) : null;
            if (previousEf)
                Assert.Equal(input, await WriteCatalogEfAsync(db, table, field, update, id, input, token));
            else await WriteCatalogSqlAsync(db, table, field, update, id, input, token);
            Assert.Equal(input is not null && input.Length > 50 ? input[..50] : input,
                await CatalogValueAsync(db, table, field, id, token));
            if (update) Assert.Equal(others, await CatalogRowAsync(db, table, id, field, token));
            else
            {
                var saved = await CatalogRowAsync(db, table, id, field, token);
                Assert.NotNull(saved);
                using var row = JsonDocument.Parse(saved);
                Assert.Equal(id, row.RootElement.GetProperty("ID").GetInt32());
                Assert.Equal(JsonValueKind.String, row.RootElement.GetProperty("CreatedDate").ValueKind);
                Assert.Equal(JsonValueKind.String, row.RootElement.GetProperty("ModifiedDate").ValueKind);
                if (table == "FileFormat")
                    Assert.Equal("Control", await CatalogValueAsync(db, table, field == "Name" ? "Extension" : "Name", id, token));
            }
            Assert.Equal(before, await CatalogSnapshotAsync(app, token, table, id));
        }
        foreach (var input in Refused(50))
        {
            var before = await CatalogSnapshotAsync(app, token);
            var id = update ? 1 : nextId++;
            PostgresException error;
            if (previousEf)
            {
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WriteCatalogEfAsync(db, table, field, update, id, input, token));
                error = Assert.IsType<PostgresException>(failure.InnerException);
            }
            else error = await Assert.ThrowsAsync<PostgresException>(() => WriteCatalogSqlAsync(db, table, field, update, id, input, token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal($"CK_{table}_{field}_SourceLength", error.ConstraintName);
            Assert.Equal(before, await CatalogSnapshotAsync(app, token));
            Assert.Null(db.Database.CurrentTransaction);
        }
    }

    [Theory]
    [InlineData("Category", "Name", false)]
    [InlineData("Category", "Name", true)]
    [InlineData("FileFormat", "Name", false)]
    [InlineData("FileFormat", "Name", true)]
    [InlineData("FileFormat", "Extension", false)]
    [InlineData("FileFormat", "Extension", true)]
    public async Task CatalogHttpGuardStillRefusesAllOverflowAfterAuthorityAndMissingLookup(string table, string field, bool update)
    {
        ValidateCatalogField(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedCatalogPaddingAsync(app, token);
        using var writer = app.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogWrite));
        using var denied = app.CreateClient();
        denied.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.CatalogRead));
        using var anonymous = app.CreateClient();
        var before = await CatalogSnapshotAsync(app, token);
        foreach (var input in Accepted(50).Where(value => value is not null && value.Length > 50).Concat(Refused(50)))
        {
            using var unauthorized = await SendCatalogAsync(anonymous, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var forbidden = await SendCatalogAsync(denied, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var refused = await SendCatalogAsync(writer, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            if (update)
            {
                using var missing = await SendCatalogAsync(writer, table, field, true, int.MaxValue, input, token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            Assert.Equal(before, await CatalogSnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData("Category", "Name", false)]
    [InlineData("Category", "Name", true)]
    [InlineData("FileFormat", "Name", false)]
    [InlineData("FileFormat", "Name", true)]
    [InlineData("FileFormat", "Extension", false)]
    [InlineData("FileFormat", "Extension", true)]
    public async Task CatalogDirectRepositoryGuardStillRefusesAllOverflowBeforeTrackedMutation(string table, string field, bool update)
    {
        ValidateCatalogField(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedCatalogPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var before = await CatalogSnapshotAsync(app, token);
        foreach (var input in Accepted(50).Where(value => value is not null && value.Length > 50).Concat(Refused(50)))
        {
            if (table == "Category")
            {
                var request = new UpsertCategoryRequest(input);
                if (update)
                {
                    await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateCategoryAsync(1, request, token));
                    Assert.False(await service.UpdateCategoryAsync(int.MaxValue, request, token));
                }
                else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateCategoryAsync(request, token));
            }
            else
            {
                var request = new UpsertFileFormatRequest(field == "Name" ? input : "Control", field == "Extension" ? input : "Control");
                if (update)
                {
                    await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateFileFormatAsync(1, request, token));
                    Assert.False(await service.UpdateFileFormatAsync(int.MaxValue, request, token));
                }
                else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFileFormatAsync(request, token));
            }
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            Assert.Equal(before, await CatalogSnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogPhysicalNullAndOmittedDefaultsRemainNullableWithoutStringDefaults(bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedCatalogPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        foreach (var (table, field) in new[] { ("Category", "Name"), ("FileFormat", "Name"), ("FileFormat", "Extension") })
        {
            foreach (var update in new[] { false, true })
            {
                var id = update ? 1 : field == "Extension" ? 21 : 20;
                var before = await CatalogSnapshotAsync(app, token, table, id);
                var others = update ? await CatalogRowAsync(db, table, id, field, token) : null;
                if (previousEf) Assert.Null(await WriteCatalogEfAsync(db, table, field, update, id, null, token));
                else await WriteCatalogSqlAsync(db, table, field, update, id, null, token);
                Assert.Null(await CatalogValueAsync(db, table, field, id, token));
                if (update) Assert.Equal(others, await CatalogRowAsync(db, table, id, field, token));
                else if (table == "FileFormat") Assert.Equal("Control", await CatalogValueAsync(db, table, field == "Name" ? "Extension" : "Name", id, token));
                Assert.Equal(before, await CatalogSnapshotAsync(app, token, table, id));
            }
        }
        foreach (var table in new[] { "Category", "FileFormat" })
        {
            var before = await CatalogSnapshotAsync(app, token, table, 30);
            var omitted = table == "Category" ? """INSERT INTO public."Category" ("ID") VALUES (30)"""
                : """INSERT INTO public."FileFormat" ("ID") VALUES (30)""";
            await db.Database.ExecuteSqlRawAsync(omitted, token);
            Assert.Null(await CatalogValueAsync(db, table, "Name", 30, token));
            if (table == "FileFormat") Assert.Null(await CatalogValueAsync(db, table, "Extension", 30, token));
            Assert.Equal(before, await CatalogSnapshotAsync(app, token, table, 30));
        }
    }

    private static void ValidateCatalogField(string table, string field)
    {
        if (!(table == "Category" && field == "Name" || table == "FileFormat" && (field is "Name" or "Extension")))
            throw new ArgumentOutOfRangeException(nameof(field));
    }

    private static Task<HttpResponseMessage> SendCatalogAsync(HttpClient client, string table, string field, bool update, int id, string? value, CancellationToken token)
    {
        ValidateCatalogField(table, field);
        var path = table == "Category" ? "/orders/categories" : "/orders/fileformats";
        object request = table == "Category" ? new UpsertCategoryRequest(value)
            : new UpsertFileFormatRequest(field == "Name" ? value : "Control", field == "Extension" ? value : "Control");
        return update ? client.PutAsJsonAsync($"{path}/{id}", request, token) : client.PostAsJsonAsync(path, request, token);
    }

    private static async Task WriteCatalogSqlAsync(OrderDbContext db, string table, string field, bool update, int id, string? value, CancellationToken token)
    {
        ValidateCatalogField(table, field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            var columns = table == "Category" ? "\"Name\"" : "\"Name\",\"Extension\"";
            var values = table == "Category" ? "@value" : field == "Name" ? "@value,'Control'" : "'Control',@value";
            command.CommandText = update ? $"UPDATE public.\"{table}\" SET \"{field}\"=@value WHERE \"ID\"=@id"
                : $"INSERT INTO public.\"{table}\" (\"ID\",{columns}) VALUES (@id,{values})";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = -1 });
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<string?> WriteCatalogEfAsync(OrderDbContext db, string table, string field, bool update, int id, string? value, CancellationToken token)
    {
        ValidateCatalogField(table, field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var previous = new CatalogPreviousContext(new DbContextOptionsBuilder<CatalogPreviousContext>()
                .UseNpgsql(db.Database.GetDbConnection(), contextOwnsConnection: false).Options);
            previous.Database.SetCommandTimeout(30);
            var category = new PreviousCategory { Id = id, Name = value };
            var format = new PreviousFileFormat { Id = id, Name = field == "Name" ? value : "Control", Extension = field == "Extension" ? value : "Control" };
            object row = table == "Category" ? category : format;
            if (update)
            {
                previous.Attach(row);
                previous.Entry(row).Property(field).IsModified = true;
            }
            else previous.Add(row);
            await previous.SaveChangesAsync(token);
            return table == "Category" ? category.Name : field == "Name" ? format.Name : format.Extension;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task SeedCatalogPaddingAsync(WebApplicationFactory<Program> app, CancellationToken token)
    {
        await SeedAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Category" ("ID","Name") VALUES (2,'Sibling category');
            INSERT INTO public."FileFormat" ("ID","Name","Extension") VALUES (2,'Sibling format','.sibling');
            UPDATE public."Order" SET "OperationKey"='catalog-retained-'||"ID"::text;
            INSERT INTO public."OrderDeletionIntent" ("OrderId","DeletionId","RequestedAtUtc","AttemptCount","NextAttemptAtUtc")
                VALUES (2,'33333333-3333-3333-3333-333333333333','2026-01-02T03:04:05Z',0,'2026-01-02T03:04:05Z');
            """, token);
    }

    private static async Task<string?> CatalogValueAsync(OrderDbContext db, string table, string field, int id, CancellationToken token)
    {
        ValidateCatalogField(table, field);
        await db.Database.OpenConnectionAsync(token);
        try { return await MigrationScalarAsync(db, $"SELECT \"{field}\" FROM public.\"{table}\" WHERE \"ID\"={id}", token); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<string?> CatalogRowAsync(OrderDbContext db, string table, int id, string excludedField, CancellationToken token)
    {
        ValidateCatalogField(table, excludedField);
        await db.Database.OpenConnectionAsync(token);
        try { return await MigrationScalarAsync(db, $"SELECT (to_jsonb(t)-'{excludedField}')::text FROM public.\"{table}\" t WHERE t.\"ID\"={id}", token); }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<string> CatalogSnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, string? excludedTable = null, int excludedId = 0)
    {
        if (excludedTable is not null) ValidateCatalogField(excludedTable, "Name");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await db.Database.OpenConnectionAsync(token);
        try
        {
            var orderRows = await MigrationScalarAsync(db, $$"""
                SELECT jsonb_build_object(
                    'orders',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Order" t),
                    'categories',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Category" t WHERE t."ID"<>{{(excludedTable == "Category" ? excludedId : 0)}}),
                    'processes',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."Process" t),
                    'formats',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."FileFormat" t WHERE t."ID"<>{{(excludedTable == "FileFormat" ? excludedId : 0)}}),
                    'files',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderFile" t),
                    'intents',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."OrderId") FROM public."OrderDeletionIntent" t))::text
                """, token);
            await statuses.Database.OpenConnectionAsync(token);
            try
            {
                var statusRows = await MigrationScalarAsync(statuses, """
                    SELECT jsonb_build_object(
                        'statuses',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatus" t),
                        'transitions',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatusHasPossibleStatus" t),
                        'history',(SELECT jsonb_agg(to_jsonb(t)||jsonb_build_object('__xmin',t.xmin::text) ORDER BY t."ID") FROM public."OrderStatusHistory" t))::text
                    """, token);
                Assert.NotNull(orderRows);
                Assert.NotNull(statusRows);
                return orderRows + "\n" + statusRows;
            }
            finally { await statuses.Database.CloseConnectionAsync(); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private sealed class PreviousCategory
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class PreviousFileFormat
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Extension { get; set; }
    }

    private sealed class CatalogPreviousContext(DbContextOptions<CatalogPreviousContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var category = builder.Entity<PreviousCategory>();
            category.ToTable("Category");
            category.HasKey(value => value.Id);
            category.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            category.Property(value => value.Name).HasMaxLength(50).HasColumnType("text");
            var format = builder.Entity<PreviousFileFormat>();
            format.ToTable("FileFormat");
            format.HasKey(value => value.Id);
            format.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            format.Property(value => value.Name).HasMaxLength(50).HasColumnType("text");
            format.Property(value => value.Extension).HasMaxLength(50).HasColumnType("text");
        }
    }
}
