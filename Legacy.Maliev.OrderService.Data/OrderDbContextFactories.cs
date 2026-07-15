using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace Legacy.Maliev.OrderService.Data;

public sealed class OrderDbContextFactory : IDesignTimeDbContextFactory<OrderDbContext> { public OrderDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrderDbContext>().UseNpgsql(Need("ConnectionStrings__OrderDbContext")).Options); private static string Need(string n) => Environment.GetEnvironmentVariable(n) ?? throw new InvalidOperationException($"{n} is required."); }
public sealed class OrderStatusDbContextFactory : IDesignTimeDbContextFactory<OrderStatusDbContext> { public OrderStatusDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<OrderStatusDbContext>().UseNpgsql(Need("ConnectionStrings__OrderStatusDbContext")).Options); private static string Need(string n) => Environment.GetEnvironmentVariable(n) ?? throw new InvalidOperationException($"{n} is required."); }
