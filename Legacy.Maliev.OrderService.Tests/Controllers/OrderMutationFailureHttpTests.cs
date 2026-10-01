using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Legacy.Maliev.OrderService.Api.Authorization;
using Legacy.Maliev.OrderService.Data;
using Legacy.Maliev.OrderService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Legacy.Maliev.OrderService.Tests.Controllers;

public sealed class OrderMutationFailureHttpTests(OrderDeletionReadinessFixture fixture) : IClassFixture<OrderDeletionReadinessFixture>
{
    [Theory]
    [InlineData("history-put")]
    [InlineData("history-delete")]
    [InlineData("transition")]
    [InlineData("file-put")]
    public async Task NormalProduction_SubmittedMutationFailureIs503AndNeverWritesTwice(string operation)
    {
        var fault = new LostAcknowledgement();
        await using var app = await fixture.AppAsync(null, configure: services =>
        {
            if (operation == "file-put") services.ConfigureDbContext<OrderDbContext>(o => o.AddInterceptors(fault));
            else services.ConfigureDbContext<OrderStatusDbContext>(o => o.AddInterceptors(fault));
        });
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        Assert.True(orders.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.True(statuses.Database.CreateExecutionStrategy().RetriesOnFailure);
        var order = new Order { Name = "Owned HTTP mutation fixture", Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } }, Quantity = 1 };
        orders.Add(order);
        await orders.SaveChangesAsync();
        var file = new OrderFile { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "original.stl" };
        orders.Add(file);
        await orders.SaveChangesAsync();
        var initial = new OrderStatus { Name = "New" };
        var target = new OrderStatus { Name = "Reviewing" };
        var history = new OrderStatusHistory { OrderId = order.Id, OrderStatus = initial };
        statuses.AddRange(history, target, new OrderStatusTransition { OrderStatus = initial, PossibleStatus = target });
        await statuses.SaveChangesAsync();
        fault.Armed = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(operation switch
        {
            "history-delete" => OrderPermissions.StatusDelete,
            "file-put" => OrderPermissions.FilesWrite,
            _ => OrderPermissions.StatusWrite,
        }));
        using var response = operation switch
        {
            "history-put" => await client.PutAsJsonAsync($"/orderstatuses/histories/{history.Id}", new { OrderId = order.Id, OrderStatusId = target.Id }),
            "history-delete" => await client.DeleteAsync($"/orderstatuses/histories/{history.Id}"),
            "file-put" => await client.PutAsJsonAsync($"/orders/files/{file.Id}", new { OrderId = order.Id, Bucket = "owned-fixture", ObjectName = "updated.stl" }),
            _ => await client.PostAsync($"/orderstatuses/histories/{order.Id}/{target.Id}", null),
        };
        Assert.True(fault.Injected, $"Normal HTTP did not reach the owned commit barrier; status {(int)response.StatusCode}.");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, fault.Commits);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Npgsql", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("acknowledgement", body, StringComparison.OrdinalIgnoreCase);
        var storedHistory = await statuses.History.AsNoTracking().ToListAsync();
        if (operation == "history-delete") Assert.Empty(storedHistory);
        else if (operation == "transition") Assert.Equal(2, storedHistory.Count);
        else if (operation == "history-put") Assert.Equal(target.Id, Assert.Single(storedHistory).OrderStatusId);
        else Assert.Equal("updated.stl", (await orders.Files.AsNoTracking().SingleAsync()).ObjectName);
        Assert.True(await orders.Orders.AsNoTracking().AnyAsync(x => x.Id == order.Id));
        Assert.Empty(await orders.DeletionIntents.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalProduction_PrecommitSerializationFailureRollsBackAndRetriesWithFreshContext(bool transition)
    {
        var fault = new PrecommitFailure();
        await using var app = await fixture.AppAsync(null, configure: services => services.ConfigureDbContext<OrderStatusDbContext>(o => o.AddInterceptors(fault.Command, fault.Rollback)));
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var order = new Order { Name = "Owned rollback fixture", Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } }, Quantity = 1 };
        orders.Add(order);
        await orders.SaveChangesAsync();
        var history = new OrderStatusHistory { OrderId = order.Id, OrderStatus = new OrderStatus { Name = "New" } };
        var target = new OrderStatus { Name = "Reviewing" };
        statuses.AddRange(history, target, new OrderStatusTransition { OrderStatus = history.OrderStatus, PossibleStatus = target });
        await statuses.SaveChangesAsync();
        fault.Armed = true;
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusWrite));
        using var response = transition
            ? await client.PostAsync($"/orderstatuses/histories/{order.Id}/{target.Id}", null)
            : await client.PutAsJsonAsync($"/orderstatuses/histories/{history.Id}", new { OrderId = order.Id, OrderStatusId = target.Id });
        Assert.Equal(transition ? HttpStatusCode.Created : HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(fault.Injected);
        Assert.True(fault.Rollbacks >= 1);
        Assert.Equal(2, fault.Contexts.Count);
        var stored = await statuses.History.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(transition ? 2 : 1, stored.Count);
        Assert.Equal(target.Id, stored.Last().OrderStatusId);
        Assert.Single(await orders.Orders.AsNoTracking().ToListAsync());
        Assert.Empty(await orders.DeletionIntents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task NormalProduction_UnknownPrecommitFailureRemains500AndRollsBack_NotSemantic409()
    {
        await using var app = await fixture.AppAsync(null);
        await using var scope = app.Services.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var statuses = scope.ServiceProvider.GetRequiredService<OrderStatusDbContext>();
        var order = new Order { Name = "Owned unknown failure fixture", Process = new Process { Name = "Owned fixture", Category = new Category { Name = "Owned fixture" } }, Quantity = 1 };
        orders.Add(order);
        await orders.SaveChangesAsync();
        var initial = new OrderStatus { Name = "New" };
        var target = new OrderStatus { Name = "Reviewing" };
        statuses.AddRange(new OrderStatusHistory { OrderId = order.Id, OrderStatus = initial }, target, new OrderStatusTransition { OrderStatus = initial, PossibleStatus = target });
        await statuses.SaveChangesAsync();
        await statuses.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_owned_unknown_transition() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'owned unknown precommit failure'; END; $$ LANGUAGE plpgsql; CREATE TRIGGER reject_owned_unknown_transition BEFORE INSERT ON \"OrderStatusHistory\" FOR EACH ROW EXECUTE FUNCTION reject_owned_unknown_transition();");
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token(OrderPermissions.StatusWrite));
        using var response = await client.PostAsync($"/orderstatuses/histories/{order.Id}/{target.Id}", null);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Npgsql", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(initial.Id, (await statuses.History.AsNoTracking().SingleAsync()).OrderStatusId);
        Assert.Single(await orders.Orders.AsNoTracking().ToListAsync());
        Assert.Empty(await orders.DeletionIntents.AsNoTracking().ToListAsync());
    }

    private sealed class PrecommitFailure
    {
        public bool Armed, Injected;
        public int Rollbacks;
        public HashSet<Guid> Contexts { get; } = [];
        public DbCommandInterceptor Command => new CommandFailure(this);
        public DbTransactionInterceptor Rollback => new RollbackObserver(this);
        private sealed class CommandFailure(PrecommitFailure owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
            {
                if (owner.Armed && (command.CommandText.StartsWith("UPDATE \"OrderStatusHistory\"", StringComparison.Ordinal)
                    || command.CommandText.StartsWith("INSERT INTO \"OrderStatusHistory\"", StringComparison.Ordinal)))
                {
                    owner.Contexts.Add(eventData.Context!.ContextId.InstanceId);
                    if (!owner.Injected)
                    {
                        owner.Injected = true;
                        throw new PostgresException("Owned precommit serialization failure", "ERROR", "ERROR", "40001");
                    }
                }
                return ValueTask.FromResult(result);
            }
        }
        private sealed class RollbackObserver(PrecommitFailure owner) : DbTransactionInterceptor
        {
            public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            {
                if (owner.Armed) owner.Rollbacks++;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class LostAcknowledgement : DbTransactionInterceptor
    {
        public bool Armed;
        public bool Injected;
        public int Commits;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Commits++;
                if (!Injected)
                {
                    Injected = true;
                    throw new NpgsqlException("Owned HTTP lost acknowledgement", new IOException("Owned transport interruption"));
                }
            }
            return Task.CompletedTask;
        }
    }
}
