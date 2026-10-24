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
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

// Physical facets only: 5fac706a7983a6d359b39acbd670e6800afe020e (parents[]),
// 72eb9f1949176392141951d35e6e06f7c30af4c2 (parent023aa2f143fa5a8f557d72c1302b9add64792d6a),
// 2aab25eb07894fc0267b03b85bad96490219d2fa (parent56014f2efc7c7f24b777c51d17a454e023d12bdd).
// Maliev.OrderService.Data/Database/OrderContext/Process.cs acbee8dadd794dfa8817e4b44fcce1c50123aacd and
// OrderContext.cs79d9f74582553559cdbb41417d7be4e1113db6d1 in that directory: required Name50.
// Maliev.OrderStatusService.Data/Database/OrderStatusContext/OrderStatus.cs625c6cfea220b94cb4176b18a17d3b9d29d25072
// and OrderStatusContext.cs3c51a831719c127eff30593f1245390cd92f1bb6 in that directory: required Name50/nullable Description100.
// Neither table has string defaults. Controller blobs d65fe4fff0427579467baf94333117776451de11 and
// 65d40f242c27d82c22359748f2beac339361b692 and full source bindings are recorded in DataMigration267/268.
// Its controlled EF8.0.28/SqlClient5.1.7 head proof has1728 observations; protected-main acceptance is still separate.
// Preserve accepted72/73,57 and primitive65/67,68/69 HTTP/repository guards; no ordinary MVC or whole-SHA claim.
public sealed partial class OrderTextPhysicalPaddingSourceTests
{
    [Theory]
    [InlineData("Process", "Name", false, false)]
    [InlineData("Process", "Name", true, false)]
    [InlineData("OrderStatus", "Name", false, false)]
    [InlineData("OrderStatus", "Name", true, false)]
    [InlineData("OrderStatus", "Description", false, false)]
    [InlineData("OrderStatus", "Description", true, false)]
    [InlineData("Process", "Name", false, true)]
    [InlineData("Process", "Name", true, true)]
    [InlineData("OrderStatus", "Name", false, true)]
    [InlineData("OrderStatus", "Name", true, true)]
    [InlineData("OrderStatus", "Description", false, true)]
    [InlineData("OrderStatus", "Description", true, true)]
    public async Task ProcessStatusPhysicalAndPreviousEfWritesNormalizeOnlyQualifiedExcessPadding(string table, string field, bool update, bool previousEf)
    {
        var maximum = ProcessStatusMaximum(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedProcessStatusPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = ProcessStatusDatabase(scope.ServiceProvider, table);
        var nextId = 20;
        foreach (var input in Accepted(maximum).OfType<string>())
        {
            var id = update ? 1 : nextId++;
            var before = await ProcessStatusSnapshotAsync(app, token, table, id);
            var others = update ? await ProcessStatusRowAsync(db, table, field, id, token) : null;
            if (previousEf) Assert.Equal(input, await WriteProcessStatusEfAsync(db, table, field, update, id, input, token));
            else await WriteProcessStatusSqlAsync(db, table, field, update, id, input, token);
            Assert.Equal(input.Length > maximum ? input[..maximum] : input,
                await ProcessStatusValueAsync(db, table, field, id, token));
            if (update) Assert.Equal(others, await ProcessStatusRowAsync(db, table, field, id, token));
            else await AssertNewProcessStatusRowAsync(db, table, field, id, token);
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token, table, id));
        }
        foreach (var input in Refused(maximum))
        {
            var before = await ProcessStatusSnapshotAsync(app, token);
            var error = await ProcessStatusWriteFailureAsync(db, table, field, update, update ? 1 : nextId++, input, previousEf, token);
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal($"CK_{table}_{field}_SourceLength", error.ConstraintName);
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token));
            Assert.Null(db.Database.CurrentTransaction);
        }
    }

    [Theory]
    [InlineData("Process", "Name", false)]
    [InlineData("Process", "Name", true)]
    [InlineData("OrderStatus", "Name", false)]
    [InlineData("OrderStatus", "Name", true)]
    [InlineData("OrderStatus", "Description", false)]
    [InlineData("OrderStatus", "Description", true)]
    public async Task ProcessStatusHttpGuardsRetainAuthorityStrictBoundsAndMissingLookup(string table, string field, bool update)
    {
        var maximum = ProcessStatusMaximum(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedProcessStatusPaddingAsync(app, token);
        using var writer = app.CreateClient();
        writer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(table == "Process" ? OrderPermissions.CatalogWrite : OrderPermissions.StatusWrite));
        using var denied = app.CreateClient();
        denied.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(table == "Process" ? OrderPermissions.CatalogRead : OrderPermissions.StatusRead));
        using var anonymous = app.CreateClient();
        var before = await ProcessStatusSnapshotAsync(app, token);
        foreach (var input in Accepted(maximum).OfType<string>().Where(value => value.Length > maximum).Concat(Refused(maximum)))
        {
            using var unauthorized = await SendProcessStatusAsync(anonymous, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var forbidden = await SendProcessStatusAsync(denied, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            using var refused = await SendProcessStatusAsync(writer, table, field, update, 1, input, token);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            if (update)
            {
                using var missing = await SendProcessStatusAsync(writer, table, field, true, int.MaxValue, input, token);
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData("Process", "Name", false)]
    [InlineData("Process", "Name", true)]
    [InlineData("OrderStatus", "Name", false)]
    [InlineData("OrderStatus", "Name", true)]
    [InlineData("OrderStatus", "Description", false)]
    [InlineData("OrderStatus", "Description", true)]
    public async Task ProcessStatusRepositoryGuardsRefuseOverflowBeforeTrackedMutation(string table, string field, bool update)
    {
        var maximum = ProcessStatusMaximum(table, field);
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedProcessStatusPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var db = ProcessStatusDatabase(scope.ServiceProvider, table);
        var before = await ProcessStatusSnapshotAsync(app, token);
        foreach (var input in Accepted(maximum).OfType<string>().Where(value => value.Length > maximum).Concat(Refused(maximum)))
        {
            if (table == "Process")
            {
                var request = new UpsertProcessRequest(1, input);
                if (update)
                {
                    await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateProcessAsync(1, request, token));
                    Assert.False(await service.UpdateProcessAsync(int.MaxValue, request, token));
                }
                else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateProcessAsync(request, token));
            }
            else
            {
                var request = new UpsertOrderStatusRequest(field == "Name" ? input : "Control", field == "Description" ? input : "Control");
                if (update)
                {
                    await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateStatusAsync(1, request, token));
                    Assert.False(await service.UpdateStatusAsync(int.MaxValue, request, token));
                }
                else await Assert.ThrowsAsync<ArgumentException>(() => service.CreateStatusAsync(request, token));
            }
            Assert.DoesNotContain(db.ChangeTracker.Entries(), entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessStatusNullOnlyRequirednessAndOmittedDefaultsRemainLiteral(bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedProcessStatusPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        foreach (var table in new[] { "Process", "OrderStatus" })
        {
            var db = ProcessStatusDatabase(scope.ServiceProvider, table);
            foreach (var update in new[] { false, true })
            {
                var before = await ProcessStatusSnapshotAsync(app, token);
                var error = await ProcessStatusWriteFailureAsync(db, table, "Name", update, update ? 1 : 20, null, previousEf, token);
                Assert.Equal(PostgresErrorCodes.NotNullViolation, error.SqlState);
                Assert.Equal(table, error.TableName);
                Assert.Equal("Name", error.ColumnName);
                Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token));
                var id = update ? 1 : 21;
                before = await ProcessStatusSnapshotAsync(app, token, table, id);
                var others = update ? await ProcessStatusRowAsync(db, table, "Name", id, token) : null;
                if (previousEf) Assert.Equal("", await WriteProcessStatusEfAsync(db, table, "Name", update, id, "", token));
                else await WriteProcessStatusSqlAsync(db, table, "Name", update, id, "", token);
                Assert.Equal("", await ProcessStatusValueAsync(db, table, "Name", id, token));
                if (update) Assert.Equal(others, await ProcessStatusRowAsync(db, table, "Name", id, token));
                else await AssertNewProcessStatusRowAsync(db, table, "Name", id, token);
                Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token, table, id));
            }
            var unchanged = await ProcessStatusSnapshotAsync(app, token);
            var omitted = table == "Process" ? """INSERT INTO public."Process" ("ID","CategoryID") VALUES (40,1)"""
                : """INSERT INTO public."OrderStatus" ("ID","Description") VALUES (40,'Control')""";
            var missingName = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(omitted, token));
            Assert.Equal(PostgresErrorCodes.NotNullViolation, missingName.SqlState);
            Assert.Equal("Name", missingName.ColumnName);
            Assert.Equal(unchanged, await ProcessStatusSnapshotAsync(app, token));
        }
        var statuses = ProcessStatusDatabase(scope.ServiceProvider, "OrderStatus");
        foreach (var update in new[] { false, true })
        {
            var id = update ? 1 : 30;
            var before = await ProcessStatusSnapshotAsync(app, token, "OrderStatus", id);
            var others = update ? await ProcessStatusRowAsync(statuses, "OrderStatus", "Description", id, token) : null;
            if (previousEf) Assert.Null(await WriteProcessStatusEfAsync(statuses, "OrderStatus", "Description", update, id, null, token));
            else await WriteProcessStatusSqlAsync(statuses, "OrderStatus", "Description", update, id, null, token);
            Assert.Null(await ProcessStatusValueAsync(statuses, "OrderStatus", "Description", id, token));
            if (update) Assert.Equal(others, await ProcessStatusRowAsync(statuses, "OrderStatus", "Description", id, token));
            else await AssertNewProcessStatusRowAsync(statuses, "OrderStatus", "Description", id, token);
            Assert.Equal(before, await ProcessStatusSnapshotAsync(app, token, "OrderStatus", id));
        }
        var omittedBefore = await ProcessStatusSnapshotAsync(app, token, "OrderStatus", 41);
        await statuses.Database.ExecuteSqlRawAsync("""INSERT INTO public."OrderStatus" ("ID","Name") VALUES (41,'Control')""", token);
        Assert.Null(await ProcessStatusValueAsync(statuses, "OrderStatus", "Description", 41, token));
        Assert.Equal(omittedBefore, await ProcessStatusSnapshotAsync(app, token, "OrderStatus", 41));
    }

    private static async Task SeedProcessStatusPaddingAsync(WebApplicationFactory<Program> app, CancellationToken token)
    {
        await SeedCatalogPaddingAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        orders.Database.SetCommandTimeout(30);
        await orders.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Process" ("ID","CategoryID","Name","CreatedDate","ModifiedDate")
                VALUES (2,2,'Sibling process',TIMESTAMP '2026-01-02 03:04:05',TIMESTAMP '2026-01-02 03:04:05');
            """, token);
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        statuses.Database.SetCommandTimeout(30);
        await statuses.Database.ExecuteSqlRawAsync("""
            UPDATE public."OrderStatus" SET "Description"=CASE "ID" WHEN 1 THEN 'Retained description' ELSE 'Sibling description' END;
            """, token);
    }

    private static int ProcessStatusMaximum(string table, string field) => (table, field) switch
    {
        ("Process", "Name") => 50,
        ("OrderStatus", "Name") => 50,
        ("OrderStatus", "Description") => 100,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static DbContext ProcessStatusDatabase(IServiceProvider services, string table) => table switch
    {
        "Process" => services.GetRequiredService<OrderDbContext>(),
        "OrderStatus" => services.GetRequiredService<OrderStatusDbContext>(),
        _ => throw new ArgumentOutOfRangeException(nameof(table)),
    };

    private static Task<HttpResponseMessage> SendProcessStatusAsync(HttpClient client, string table, string field, bool update, int id, string value, CancellationToken token)
    {
        _ = ProcessStatusMaximum(table, field);
        var path = table == "Process" ? "/orders/processes" : "/orderstatuses";
        object request = table == "Process" ? new UpsertProcessRequest(1, value)
            : new UpsertOrderStatusRequest(field == "Name" ? value : "Control", field == "Description" ? value : "Control");
        return update ? client.PutAsJsonAsync($"{path}/{id}", request, token) : client.PostAsJsonAsync(path, request, token);
    }

    private static async Task<PostgresException> ProcessStatusWriteFailureAsync(DbContext db, string table, string field, bool update, int id, string? value, bool previousEf, CancellationToken token)
    {
        if (!previousEf) return await Assert.ThrowsAsync<PostgresException>(() => WriteProcessStatusSqlAsync(db, table, field, update, id, value, token));
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WriteProcessStatusEfAsync(db, table, field, update, id, value, token));
        return Assert.IsType<PostgresException>(failure.InnerException);
    }

    private static async Task WriteProcessStatusSqlAsync(DbContext db, string table, string field, bool update, int id, string? value, CancellationToken token)
    {
        _ = ProcessStatusMaximum(table, field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            var columns = table == "Process" ? "\"CategoryID\",\"Name\"" : "\"Name\",\"Description\"";
            var values = table == "Process" ? "1,@value" : field == "Name" ? "@value,'Control'" : "'Control',@value";
            command.CommandText = update ? $"UPDATE public.\"{table}\" SET \"{field}\"=@value WHERE \"ID\"=@id"
                : $"INSERT INTO public.\"{table}\" (\"ID\",{columns}) VALUES (@id,{values})";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = -1 });
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task<string?> WriteProcessStatusEfAsync(DbContext db, string table, string field, bool update, int id, string? value, CancellationToken token)
    {
        _ = ProcessStatusMaximum(table, field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            var capture = new ProcessStatusParameterCapture(table, field, value);
            await using var previous = new ProcessStatusPreviousContext(new DbContextOptionsBuilder<ProcessStatusPreviousContext>()
                .UseNpgsql(db.Database.GetDbConnection(), contextOwnsConnection: false).AddInterceptors(capture).Options);
            previous.Database.SetCommandTimeout(30);
            var process = new ProcessStatusPreviousProcess { Id = id, CategoryId = 1, Name = value };
            var status = new ProcessStatusPreviousStatus { Id = id, Name = field == "Name" ? value : "Control", Description = field == "Description" ? value : "Control" };
            object row = table == "Process" ? process : status;
            if (update)
            {
                previous.Attach(row);
                previous.Entry(row).Property(field).IsModified = true;
            }
            else previous.Add(row);
            try { await previous.SaveChangesAsync(token); }
            catch (DbUpdateException) { Assert.Equal(1, capture.ValidatedCommands); throw; }
            Assert.Equal(1, capture.ValidatedCommands);
            return table == "Process" ? process.Name : field == "Name" ? status.Name : status.Description;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static Task<string?> ProcessStatusValueAsync(DbContext db, string table, string field, int id, CancellationToken token)
    {
        _ = ProcessStatusMaximum(table, field);
        return ProcessStatusScalarAsync(db, $"SELECT \"{field}\" FROM public.\"{table}\" WHERE \"ID\"={id}", token);
    }

    private static Task<string?> ProcessStatusRowAsync(DbContext db, string table, string field, int id, CancellationToken token)
    {
        _ = ProcessStatusMaximum(table, field);
        return ProcessStatusScalarAsync(db, $"SELECT (to_jsonb(t)-'{field}')::text FROM public.\"{table}\" t WHERE t.\"ID\"={id}", token);
    }

    private static async Task<string?> ProcessStatusScalarAsync(DbContext db, string sql, CancellationToken token)
    {
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(token);
            return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static async Task AssertNewProcessStatusRowAsync(DbContext db, string table, string field, int id, CancellationToken token)
    {
        var saved = await ProcessStatusRowAsync(db, table, field, id, token);
        Assert.NotNull(saved);
        using var row = JsonDocument.Parse(saved);
        Assert.Equal(id, row.RootElement.GetProperty("ID").GetInt32());
        Assert.Equal(JsonValueKind.String, row.RootElement.GetProperty("CreatedDate").ValueKind);
        Assert.Equal(JsonValueKind.String, row.RootElement.GetProperty("ModifiedDate").ValueKind);
        if (table == "Process") Assert.Equal(1, row.RootElement.GetProperty("CategoryID").GetInt32());
        else Assert.Equal("Control", row.RootElement.GetProperty(field == "Name" ? "Description" : "Name").GetString());
    }

    private static async Task<string> ProcessStatusSnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, string? excludedTable = null, int excludedId = 0)
    {
        if (excludedTable is not null) _ = ProcessStatusMaximum(excludedTable, "Name");
        var parts = (await CatalogSnapshotAsync(app, token)).Split('\n');
        Assert.Equal(2, parts.Length);
        using var orders = JsonDocument.Parse(parts[0]);
        using var statuses = JsonDocument.Parse(parts[1]);
        return JsonSerializer.Serialize(new[] { orders.RootElement, statuses.RootElement }.Select(document =>
            document.EnumerateObject().ToDictionary(property => property.Name, property =>
                excludedTable == "Process" && property.Name == "processes" || excludedTable == "OrderStatus" && property.Name == "statuses"
                    ? JsonSerializer.SerializeToElement(property.Value.EnumerateArray().Where(row => row.GetProperty("ID").GetInt32() != excludedId).ToArray())
                    : property.Value)));
    }

    private sealed class ProcessStatusParameterCapture(string table, string field, string? expectedValue) : DbCommandInterceptor
    {
        public int ValidatedCommands { get; private set; }

        private void Capture(DbCommand command)
        {
            _ = ProcessStatusMaximum(table, field);
            Assert.InRange(command.CommandText.Length, 1, 16_384);
            var target = "(?:\"public\"\\.)?\"" + Regex.Escape(table) + "\"";
            Assert.True(Regex.IsMatch(command.CommandText, "(?:UPDATE|INSERT\\s+INTO)\\s+" + target,
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            var update = Regex.Match(command.CommandText, "\"" + Regex.Escape(field) + "\"\\s*=\\s*(?<parameter>@\\w+)",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            string selected;
            if (update.Success) selected = update.Groups["parameter"].Value;
            else
            {
                var insert = Regex.Match(command.CommandText,
                    "INSERT\\s+INTO\\s+" + target + "\\s*\\((?<columns>[^)]*)\\)\\s*VALUES\\s*\\((?<values>[^)]*)\\)",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                Assert.True(insert.Success);
                var columns = insert.Groups["columns"].Value.Split(',').Select(column => column.Trim().Trim('"')).ToArray();
                var values = insert.Groups["values"].Value.Split(',').Select(parameter => parameter.Trim()).ToArray();
                Assert.Equal(columns.Length, values.Length);
                var index = Array.IndexOf(columns, field);
                Assert.InRange(index, 0, values.Length - 1);
                selected = values[index];
            }
            var parameter = Assert.Single(command.Parameters.Cast<NpgsqlParameter>(), value => value.ParameterName.TrimStart('@') == selected.TrimStart('@'));
            Assert.Equal(NpgsqlDbType.Text, parameter.NpgsqlDbType);
            Assert.True(parameter.Size <= 0 || parameter.Size >= (expectedValue?.Length ?? 0), "The selected EF parameter must not restrict the full input length.");
            Assert.Equal(expectedValue, parameter.Value is null or DBNull ? null : Assert.IsType<string>(parameter.Value));
            ValidatedCommands++;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ProcessStatusPreviousProcess
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ProcessStatusPreviousStatus
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private sealed class ProcessStatusPreviousContext(DbContextOptions<ProcessStatusPreviousContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var process = builder.Entity<ProcessStatusPreviousProcess>();
            process.ToTable("Process");
            process.HasKey(value => value.Id);
            process.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            process.Property(value => value.CategoryId).HasColumnName("CategoryID");
            process.Property(value => value.Name).IsRequired().HasMaxLength(50).HasColumnType("text");
            var status = builder.Entity<ProcessStatusPreviousStatus>();
            status.ToTable("OrderStatus");
            status.HasKey(value => value.Id);
            status.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            status.Property(value => value.Name).IsRequired().HasMaxLength(50).HasColumnType("text");
            status.Property(value => value.Description).HasMaxLength(100).HasColumnType("text");
        }
    }
}
