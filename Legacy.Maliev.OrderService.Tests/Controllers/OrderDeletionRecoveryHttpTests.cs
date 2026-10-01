using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderDeletionRecoveryHttpTests(OrderDeletionReadinessFixture fixture) : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NormalProduction_LostOrderAcknowledgementReconcilesExactReceiptAndCompletes(int commitNumber)
    {
        var fault = new CommitFault(commitNumber);
        await using var app = await fixture.AppAsync(null, configure: services => services.ConfigureDbContext<OrderDbContext>(o => o.AddInterceptors(fault)), recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.True(orders.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.True(statuses.Database.CreateExecutionStrategy().RetriesOnFailure);
        var id = await SeedAsync(orders, statuses);
        fault.Armed = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var response = await client.DeleteAsync($"/orders/{id}");
        Assert.True(fault.Injected);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var receipt = await orders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        Assert.NotNull(receipt.CompletedAtUtc);
        Assert.NotNull(receipt.StatusCleanupCompletedAtUtc);
        Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Empty(await orders.Files.AsNoTracking().ToListAsync());
        Assert.Empty(await statuses.History.AsNoTracking().ToListAsync());
        using var replay = await client.DeleteAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        Assert.Equal(receipt.DeletionId, (await orders.DeletionIntents.AsNoTracking().SingleAsync()).DeletionId);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task LostAcknowledgement_UnavailableOrInvalidFreshReceiptNeverAuthorizesSuccess(int commitNumber, bool corruptReceipt)
    {
        var fault = new ReconciliationFault(commitNumber, corruptReceipt);
        await using var app = await fixture.AppAsync(null, configure: services => services.ConfigureDbContext<OrderDbContext>(o => o.AddInterceptors(fault.Commit, fault.Read)), recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(orders, statuses);
        fault.Connection = orders.Database.GetConnectionString()!;
        fault.Armed = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var response = await client.DeleteAsync($"/orders/{id}");
        Assert.True(fault.CommitInjected);
        Assert.Equal(corruptReceipt ? HttpStatusCode.Conflict : HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(!corruptReceipt, fault.ReadInjected);
        Assert.DoesNotContain("Npgsql", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        await using var fresh = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(fault.Connection).Options);
        Assert.Empty(await fresh.Orders.AsNoTracking().ToListAsync());
        Assert.Empty(await fresh.Files.AsNoTracking().ToListAsync());
        var receipt = await fresh.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.NotEqual(Guid.Empty, receipt.DeletionId);
        if (commitNumber == 1)
        {
            Assert.Null(receipt.CompletedAtUtc);
            Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        }
        else
        {
            Assert.NotNull(receipt.CompletedAtUtc);
            Assert.Empty(await statuses.History.AsNoTracking().ToListAsync());
        }
    }

    private sealed class ReconciliationFault(int number, bool corrupt)
    {
        private readonly int target = number;
        private readonly bool corruptReceipt = corrupt;
        public bool Armed, CommitInjected, ReadInjected;
        public string Connection = string.Empty;
        private int commits;
        public DbTransactionInterceptor Commit => new CommitObserver(this);
        public DbCommandInterceptor Read => new ReadFailure(this);
        private sealed class CommitObserver(ReconciliationFault owner) : DbTransactionInterceptor
        {
            public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                if (!owner.Armed || ++owner.commits != owner.target || owner.CommitInjected) return;
                owner.CommitInjected = true;
                if (owner.corruptReceipt)
                {
                    await using var context = new OrderDbContext(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(owner.Connection).Options);
                    await context.DeletionIntents.ExecuteUpdateAsync(s => s.SetProperty(x => x.StatusCleanupCompletedAtUtc, (DateTime?)null), cancellationToken);
                }
                throw new PostgresException("Owned nonretryable lost acknowledgement", "ERROR", "ERROR", "P0001");
            }
        }
        private sealed class ReadFailure(ReconciliationFault owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                if (!owner.corruptReceipt && owner.CommitInjected && !owner.ReadInjected && command.CommandText.Contains("OrderDeletionIntent", StringComparison.Ordinal))
                {
                    owner.ReadInjected = true;
                    throw new PostgresException("Owned authoritative read unavailable", "ERROR", "ERROR", "P0001");
                }
                return ValueTask.FromResult(result);
            }
        }
    }

    [Fact]
    public async Task StatusUnavailable503_PreservesPendingAuthorityAndFreshHttpRetryCompletes()
    {
        await using var app = await fixture.AppAsync(null, recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(orders, statuses);
        await statuses.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_owned_http_cleanup() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'owned disposable cleanup fault'; END; $$ LANGUAGE plpgsql; CREATE TRIGGER reject_owned_http_cleanup BEFORE DELETE ON \"OrderStatusHistory\" FOR EACH ROW EXECUTE FUNCTION reject_owned_http_cleanup();");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        Guid receiptId;
        try
        {
            using var failed = await client.DeleteAsync($"/orders/{id}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var pending = await orders.DeletionIntents.AsNoTracking().SingleAsync();
            receiptId = pending.DeletionId;
            Assert.Null(pending.CompletedAtUtc);
            Assert.True(pending.AttemptCount >= 1);
            Assert.True(pending.NextAttemptAtUtc >= pending.RequestedAtUtc);
            Assert.False(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
            Assert.Empty(await orders.Files.AsNoTracking().ToListAsync());
            Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
            Assert.DoesNotContain("Npgsql", await failed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
        finally { await statuses.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_owned_http_cleanup ON \"OrderStatusHistory\"; DROP FUNCTION reject_owned_http_cleanup();"); }
        using var retry = await client.DeleteAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        var completed = await orders.DeletionIntents.AsNoTracking().SingleAsync();
        Assert.Equal(receiptId, completed.DeletionId);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Empty(await statuses.History.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task LiveOrderWithIntent_Is409WithoutAnyGraphMutation()
    {
        await using var app = await fixture.AppAsync(null, recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(orders, statuses);
        orders.DeletionIntents.Add(new OrderDeletionIntent { OrderId = id, DeletionId = Guid.NewGuid(), RequestedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow });
        await orders.SaveChangesAsync();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var response = await client.DeleteAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Single(await orders.Files.AsNoTracking().ToListAsync());
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        Assert.Null((await orders.DeletionIntents.AsNoTracking().SingleAsync()).CompletedAtUtc);
    }

    [Theory]
    [InlineData("previous")]
    [InlineData("missing-index")]
    public async Task EnabledButPhysicalAuthorityMissing_Is503WithoutUnsafeFallback(string drift)
    {
        await using var app = await fixture.AppAsync(drift, recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(orders, statuses);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.DeleteToken());
        using var response = await client.DeleteAsync($"/orders/{id}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Single(await orders.Files.AsNoTracking().ToListAsync());
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryJwtAndPermissionDenialsRemainUnchanged(bool signedWrongPermission)
    {
        await using var app = await fixture.AppAsync(null, recoveryEnabled: true);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var id = await SeedAsync(orders, statuses);
        using var client = app.CreateClient();
        if (signedWrongPermission) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.Read));
        using var response = await client.DeleteAsync($"/orders/{id}");
        Assert.Equal(signedWrongPermission ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == id));
        Assert.Single(await statuses.History.AsNoTracking().ToListAsync());
        Assert.Empty(await orders.DeletionIntents.AsNoTracking().ToListAsync());
    }

    private static async Task<int> SeedAsync(OrderDbContext orders, OrderStatusDbContext statuses)
    {
        var order = new Order { Name = "Owned HTTP deletion fixture", Quantity = 1, Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } } };
        orders.Add(order);
        await orders.SaveChangesAsync();
        orders.Files.Add(new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "owned.stl" });
        await orders.SaveChangesAsync();
        statuses.History.Add(new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } });
        await statuses.SaveChangesAsync();
        return order.Id;
    }

    private sealed class CommitFault(int number) : DbTransactionInterceptor
    {
        public bool Armed;
        public bool Injected;
        private int commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed && Interlocked.Increment(ref commits) == number && !Injected)
            {
                Injected = true;
                throw new NpgsqlException("Owned deletion lost acknowledgement", new IOException("Owned interruption"));
            }
            return Task.CompletedTask;
        }
    }
}
