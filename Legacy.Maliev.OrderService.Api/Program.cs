using System.Text.Json.Serialization;
using Legacy.Maliev.OrderService.Api;
using Legacy.Maliev.OrderService.Api.Serialization;
using Legacy.Maliev.OrderService.Application.Interfaces;
using Legacy.Maliev.OrderService.Data;
using Maliev.Aspire.ServiceDefaults;
// These registrations do not activate recovery; both admission options remain default-off.
static void AddDeletionRecovery(IServiceCollection services)
{
    services.AddScoped<IOrderDeletionRecovery>(p => (OrderRepository)p.GetRequiredService<IOrderService>());
    services.AddOptions<OrderDeletionWorkerOptions>().BindConfiguration("OrderDeletionWorker");
    services.AddHostedService<OrderDeletionRecoveryWorker>();
}
var b = WebApplication.CreateBuilder(args); b.AddServiceDefaults(); b.AddPrivateRequestObservation("order"); b.AddDefaultApiVersioning(); b.AddPostgresDbContext<OrderDbContext>(connectionName: "OrderDbContext"); b.AddPostgresDbContext<OrderStatusDbContext>(connectionName: "OrderStatusDbContext"); b.AddStandardCache("legacy:order:"); b.AddStandardCors(); b.AddJwtAuthentication(); b.AddStandardMiddleware(o => o.EnableRequestLogging = true); b.AddStandardOpenApi(title: "Legacy MALIEV Order Service API", description: "Temporary .NET 10 compatibility API for order and order-status contracts."); b.Services.AddControllers().AddJsonOptions(o => { o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; o.JsonSerializerOptions.PropertyNamingPolicy = null; o.JsonSerializerOptions.DictionaryKeyPolicy = null; }); // OpenAPI reads HTTP JSON options separately from MVC's serializer options.
b.Services.ConfigureOptions<OrderSourceBodyInputFormatterSetup>();
b.Services.ConfigureOptions<OrderRootBodyInputFormatterSetup>();
b.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.PropertyNamingPolicy = null;
    o.SerializerOptions.DictionaryKeyPolicy = null;
});
// Register in this assembly so its generated XML contract support is activated.
b.Services.AddOpenApi("v1", options => options.AddSchemaTransformer((schema, context, _) =>
{
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Application.Models.FileFormatResponse))
        foreach (var propertyName in new[] { "name", "extension", "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.OrderHistoryStatusResponse)
        || context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.OrderStatusReadResponse)
        || context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.OrderLatestStatusReadResponse))
        foreach (var propertyName in new[] { "name", "description", "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.OrderLatestHistoryReadResponse))
        foreach (var propertyName in new[] { "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.ProcessReadResponse))
        foreach (var propertyName in new[] { "category", "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.ProcessCategoryReadResponse))
        foreach (var propertyName in new[] { "name", "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    if (context.JsonTypeInfo.Type == typeof(Legacy.Maliev.OrderService.Api.Controllers.CategoryReadResponse))
        foreach (var propertyName in new[] { "name", "createdDate", "modifiedDate" })
            schema.Required?.Remove(propertyName);
    return Task.CompletedTask;
}));
b.Services.AddSingleton(TimeProvider.System); b.Services.AddScoped<DistributedOrderCache>(); b.Services.AddScoped<IOrderCache>(p => p.GetRequiredService<DistributedOrderCache>()); b.Services.AddScoped<IIdempotencyStore>(p => p.GetRequiredService<DistributedOrderCache>()); b.Services.AddScoped<IOrderService, OrderRepository>(); b.Services.AddOptions<OrderDeletionRecoveryOptions>().BindConfiguration("OrderDeletionRecovery"); b.Services.AddScoped<OrderDeletionAdmissionFilter>(); b.Services.AddHealthChecks().AddCheck<OrderDeletionReadinessHealthCheck>("order-deletion-schema", tags: ["ready"]); AddDeletionRecovery(b.Services); var app = b.Build(); app.UseStandardMiddleware(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization(); app.MapDefaultEndpoints("order"); app.MapControllers(); app.MapApiDocumentation(servicePrefix: "order"); await app.RunAsync(); public partial class Program;
