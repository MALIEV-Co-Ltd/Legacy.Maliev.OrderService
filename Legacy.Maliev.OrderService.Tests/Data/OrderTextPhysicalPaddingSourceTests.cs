using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Application.Models;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Legacy.Maliev.OrderService.Tests.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Legacy.Maliev.OrderService.Tests.Data;

// Physical source: 5fac706a7983a6d359b39acbd670e6800afe020e (root, no parents),
// Maliev.OrderService.Data/Database/OrderContext/OrderContext.cs blob130b92275a21a2ff9b5b6ac3d1d92f0e1074a001.
// Latest context at d23065d499333ece5ef73013b085a8aaa062b163 (parent449d87ea9d1d6ab40f1ec444353ab6aba80a5a76)
// is blob79d9f74582553559cdbb41417d7be4e1113db6d1: nullable Name100/defaultunnamed and Description250.
// HTTP guards are a later, separate source contract: 7b311e4e7f0dd80be0441abc2625dab295179f1a
// (parent0fc91415d35550b48b002c2a36b01ec64089b1a8), Maliev.OrderService.Api/Controllers/OrdersController.cs
// blob4d4d734ac28966fbe6b6b7d2861fd741bbd5b9e3; every overlong Name/Description is refused before lookup.
// DataMigration263's current EF8.0.28 ANSI-ON synthetic witness qualifies physical padding only;
// this is neither an original deployment/EF8.0.3 claim nor permission to broaden the HTTP guards.
public sealed partial class OrderTextPhysicalPaddingSourceTests(OrderDeletionReadinessFixture fixture)
    : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("Name", false, false)]
    [InlineData("Name", true, false)]
    [InlineData("Description", false, false)]
    [InlineData("Description", true, false)]
    [InlineData("Name", false, true)]
    [InlineData("Name", true, true)]
    [InlineData("Description", false, true)]
    [InlineData("Description", true, true)]
    public async Task PhysicalAndPreviousEfWritesPreserveOnlyQualifiedExcessPadding(string field, bool update, bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var maximum = Maximum(field);
        var nextId = 20;
        foreach (var input in Accepted(maximum))
        {
            var id = update ? 1 : nextId++;
            var before = await SnapshotAsync(app, token, id);
            var others = update ? OtherFields(await db.Orders.AsNoTracking().SingleAsync(row => row.Id == id, token), field) : null;
            if (previousEf)
            {
                var immediate = await WriteEfAsync(db, field, update, id, input, token);
                Assert.Equal(field == "Name" && !update && input is null ? "unnamed" : input, immediate);
            }
            else await WriteSqlAsync(db, field, update, id, input, token);
            var stored = await db.Orders.AsNoTracking().SingleAsync(row => row.Id == id, token);
            var expected = field == "Name" && previousEf && !update && input is null ? "unnamed"
                : input is not null && input.Length > maximum ? input[..maximum] : input;
            Assert.Equal(expected, field == "Name" ? stored.Name : stored.Description);
            if (update) Assert.Equal(others, OtherFields(stored, field));
            else AssertNewRow(stored, field);
            Assert.Equal(before, await SnapshotAsync(app, token, id));
        }

        foreach (var input in Refused(maximum))
        {
            var before = await SnapshotAsync(app, token);
            PostgresException error;
            if (previousEf)
            {
                var failure = await Assert.ThrowsAsync<DbUpdateException>(() => WriteEfAsync(db, field, update, update ? 1 : nextId++, input, token));
                error = Assert.IsType<PostgresException>(failure.InnerException);
            }
            else error = await Assert.ThrowsAsync<PostgresException>(() => WriteSqlAsync(db, field, update, update ? 1 : nextId++, input, token));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal($"CK_Order_{field}_SourceLength", error.ConstraintName);
            Assert.Equal(before, await SnapshotAsync(app, token));
        }
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("Name", true)]
    [InlineData("Description", false)]
    [InlineData("Description", true)]
    public async Task LaterSourceHttpGuardStillRefusesAllOverflowBeforeAnyWriteOrMissingLookup(string field, bool update)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedAsync(app, token);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(update ? OrderPermissions.Update : OrderPermissions.Create));
        var maximum = Maximum(field);
        foreach (var input in Accepted(maximum).Where(value => value is not null && value.Length > maximum).Concat(Refused(maximum)))
        {
            var request = JsonSerializer.Deserialize<UpsertOrderRequest>(JsonSerializer.Serialize(new
            {
                Name = field == "Name" ? input : "Control",
                Description = field == "Description" ? input : "Control description",
                ProcessId = 1,
                Quantity = 1,
            }))!;
            var before = await SnapshotAsync(app, token);
            foreach (var id in update ? new[] { 1, int.MaxValue } : new[] { 0 })
            {
                using var response = update
                    ? await client.PutAsJsonAsync($"/orders/{id}", request, token)
                    : await client.PostAsJsonAsync("/orders", request, token);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                var error = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(token);
                Assert.NotNull(error);
                Assert.Contains(field, error.Errors.Keys);
                Assert.Equal(before, await SnapshotAsync(app, token));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhysicalNullDefaultAndPreviousEfGeneratedNameRemainDistinct(bool previousEf)
    {
        await using var app = await fixture.AppAsync(null);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = lifetime.Token;
        await SeedAsync(app, token);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var before = await SnapshotAsync(app, token, 1, 20, 21, 22);
        var targetOthers = OtherFields(await db.Orders.AsNoTracking().SingleAsync(row => row.Id == 1, token), "Name");
        if (previousEf)
        {
            Assert.Equal("unnamed", await WriteEfAsync(db, "Name", false, 20, null, token));
            Assert.Null(await WriteEfAsync(db, "Name", true, 1, null, token));
        }
        else
        {
            await WriteSqlAsync(db, "Name", false, 20, null, token);
            await WriteSqlAsync(db, "Name", true, 1, null, token);
        }
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment") VALUES (21,1,1,0,false,false,false);
            INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment","Name") VALUES (22,1,1,0,false,false,false,DEFAULT);
            """, token);
        var rows = await db.Orders.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token);
        Assert.Null(Assert.Single(rows, row => row.Id == 1).Name);
        Assert.Equal(targetOthers, OtherFields(Assert.Single(rows, row => row.Id == 1), "Name"));
        Assert.Equal(previousEf ? "unnamed" : null, Assert.Single(rows, row => row.Id == 20).Name);
        AssertNewRow(Assert.Single(rows, row => row.Id == 20), "Name");
        Assert.Equal("unnamed", Assert.Single(rows, row => row.Id == 21).Name);
        Assert.Equal("unnamed", Assert.Single(rows, row => row.Id == 22).Name);
        Assert.Equal(before, await SnapshotAsync(app, token, 1, 20, 21, 22));
    }

    private static int Maximum(string field) => field switch
    {
        "Name" => 100,
        "Description" => 250,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string?[] Accepted(int maximum) => [null, "", "  ไทย 😀  ", new string('ก', maximum),
        string.Concat(Enumerable.Repeat("😀", maximum / 2)), new string('A', maximum - 2) + "  ",
        new string('A', maximum) + " ", string.Concat(Enumerable.Repeat("😀", maximum / 2)) + " ",
        string.Concat(Enumerable.Repeat("😀", maximum / 2 - 1)) + "   "];

    private static string[] Refused(int maximum) => [new string('A', maximum + 1),
        string.Concat(Enumerable.Repeat("😀", maximum / 2 + 1)), new string('A', maximum) + "\u00a0",
        new string('A', maximum) + "\t", new string('A', maximum - 1) + "😀 "];

    private static string OtherFields(Order row, string field) => JsonSerializer.Serialize(
        JsonSerializer.SerializeToElement(row).EnumerateObject().Where(value => value.Name != field)
            .ToDictionary(value => value.Name, value => value.Value));

    private static void AssertNewRow(Order row, string field)
    {
        Assert.Equal("Control", field == "Name" ? row.Description : row.Name);
        Assert.Equal(1, row.ProcessId);
        Assert.Equal(1, row.Quantity);
        Assert.Equal(0, row.Manufactured);
        Assert.Equal(1, row.Remaining);
        Assert.Null(row.CustomerId);
        Assert.Null(row.EmployeeId);
        Assert.Null(row.MaterialId);
        Assert.Null(row.SurfaceFinishId);
        Assert.Null(row.ColorId);
        Assert.Null(row.CurrencyId);
        Assert.Null(row.UnitPrice);
        Assert.Null(row.DiscountPercent);
        Assert.Null(row.Subtotal);
        Assert.Null(row.LeadTime);
        Assert.Null(row.PromisedDate);
        Assert.Null(row.FinishedDate);
        Assert.Null(row.Turnaround);
        Assert.Null(row.Comment);
        Assert.Null(row.TrackingNumber);
        Assert.Null(row.OperationKey);
        Assert.False(row.AllowSocialMedia);
        Assert.False(row.AllowCancellation);
        Assert.False(row.AllowPayment);
        Assert.NotNull(row.CreatedDate);
        Assert.NotNull(row.ModifiedDate);
    }

    private static async Task WriteSqlAsync(OrderDbContext db, string field, bool update, int id, string? value, CancellationToken token)
    {
        _ = Maximum(field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 30;
            command.CommandText = update ? $"UPDATE public.\"Order\" SET \"{field}\"=@value WHERE \"ID\"=@id"
                : $"INSERT INTO public.\"Order\" (\"ID\",\"ProcessID\",\"Quantity\",\"Manufactured\",\"AllowSocialMedia\",\"AllowCancellation\",\"AllowPayment\",\"Name\",\"Description\") VALUES (@id,1,1,0,false,false,false,{(field == "Name" ? "@value,'Control'" : "'Control',@value")})";
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer) { Value = id });
            command.Parameters.Add(new NpgsqlParameter("value", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value, Size = -1 });
            Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> WriteEfAsync(OrderDbContext db, string field, bool update, int id, string? value, CancellationToken token)
    {
        _ = Maximum(field);
        await db.Database.OpenConnectionAsync(token);
        try
        {
            await using var old = new PreviousContext(new DbContextOptionsBuilder<PreviousContext>()
                .UseNpgsql(db.Database.GetDbConnection(), contextOwnsConnection: false).Options);
            old.Database.SetCommandTimeout(30);
            var row = new PreviousOrder
            {
                Id = id,
                ProcessId = 1,
                Quantity = 1,
                Name = field == "Name" ? value : "Control",
                Description = field == "Description" ? value : "Control",
            };
            if (update)
            {
                old.Attach(row);
                old.Entry(row).Property(field).IsModified = true;
            }
            else old.Add(row);
            await old.SaveChangesAsync(token);
            return field == "Name" ? row.Name : row.Description;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task SeedAsync(WebApplicationFactory<Program> app, CancellationToken token)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        db.Database.SetCommandTimeout(30);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Category" ("ID","Name") VALUES (1,'Text category');
            INSERT INTO public."Process" ("ID","CategoryID","Name") VALUES (1,1,'Text process');
            INSERT INTO public."Order" ("ID","ProcessID","Quantity","Manufactured","AllowSocialMedia","AllowCancellation","AllowPayment","Name","Description","TrackingNumber") VALUES (1,1,2,0,false,false,false,'Target','Target description','Retained tracking'),(2,1,3,0,false,false,false,'Sibling','Sibling description','Sibling tracking');
            INSERT INTO public."FileFormat" ("ID","Name","Extension") VALUES (1,'Retained','.step');
            INSERT INTO public."OrderFile" ("ID","OrderID","Bucket","ObjectName") VALUES (1,2,'retained','part.step');
            """, token);
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        await statuses.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."OrderStatus" ("ID","Name") VALUES (1,'Retained'),(2,'Next');
            INSERT INTO public."OrderStatusHasPossibleStatus" ("ID","OrderStatusID","PossibleStatusID") VALUES (1,1,2);
            INSERT INTO public."OrderStatusHistory" ("ID","OrderID","OrderStatusID") VALUES (1,2,1);
            """, token);
    }

    private static async Task<string> SnapshotAsync(WebApplicationFactory<Program> app, CancellationToken token, params int[] excluded)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        return JsonSerializer.Serialize(new
        {
            Orders = await db.Orders.AsNoTracking().Where(row => !excluded.Contains(row.Id)).OrderBy(row => row.Id).ToArrayAsync(token),
            Categories = await db.Categories.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Processes = await db.Processes.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Formats = await db.FileFormats.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Files = await db.Files.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Intents = await db.DeletionIntents.AsNoTracking().OrderBy(row => row.OrderId).ToArrayAsync(token),
            Statuses = await statuses.Statuses.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            Transitions = await statuses.Transitions.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
            History = await statuses.History.AsNoTracking().OrderBy(row => row.Id).ToArrayAsync(token),
        });
    }

    private sealed class PreviousOrder
    {
        public int Id { get; set; }
        public int ProcessId { get; set; }
        public int Quantity { get; set; }
        public int Manufactured { get; set; }
        public bool AllowSocialMedia { get; set; }
        public bool AllowCancellation { get; set; }
        public bool AllowPayment { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    private sealed class PreviousContext(DbContextOptions<PreviousContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            var row = builder.Entity<PreviousOrder>();
            row.ToTable("Order");
            row.HasKey(value => value.Id);
            row.Property(value => value.Id).HasColumnName("ID").ValueGeneratedNever();
            row.Property(value => value.ProcessId).HasColumnName("ProcessID");
            row.Property(value => value.Name).HasMaxLength(100).HasDefaultValue("unnamed").HasColumnType("text");
            row.Property(value => value.Description).HasMaxLength(250).HasColumnType("text");
        }
    }
}
