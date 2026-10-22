using System.Buffers;
using System.Text;
using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using SourceCategory = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext.Category;
using SourceFormat = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext.FileFormat;
using SourceProcess = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext.Process;
using SourceFile = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderContext.OrderFile;
using SourceStatus = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderStatusContext.OrderStatus;
using SourceHistory = Legacy.Maliev.OrderService.Api.Serialization.SourceBodies.OrderStatusContext.OrderStatusHistory;

namespace Legacy.Maliev.OrderService.Api.Serialization;

/// <summary>Adds source body admission for the six owned non-Order request contracts.</summary>
/// <param name="loggers">The application logger factory.</param>
/// <param name="pools">The application object pool provider.</param>
public sealed class OrderSourceBodyInputFormatterSetup(ILoggerFactory loggers, ObjectPoolProvider pools) : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options)
    {
        if (options.InputFormatters.OfType<OrderSourceBodyInputFormatter>().Any()) return;
        var source = new MvcNewtonsoftJsonOptions();
        source.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        source.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        // Stock MVC defaults retain MaxDepth32, TypeNameHandling.None and
        // MetadataPropertyHandling.Default. Never install global Newtonsoft options.
        var logger = new SourceBodyLogger(loggers.CreateLogger<OrderSourceBodyInputFormatterSetup>());
        options.InputFormatters.Insert(0, new OrderSourceBodyInputFormatter(
            new BufferedSourceFormatter(logger, source, pools, options)));
    }

    private sealed class SourceBodyLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            target.BeginScope("Order source body admission");
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (target.IsEnabled(logLevel))
                target.Log(logLevel, eventId, "SourceBodyAdmissionFailure ExceptionType={ExceptionType}",
                    exception?.GetType().Name ?? "InputFormatter");
        }
    }

    private sealed class BufferedSourceFormatter(
        ILogger logger, MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, MvcOptions mvc)
        : NewtonsoftJsonInputFormatter(logger, json.SerializerSettings, ArrayPool<char>.Shared, pools, mvc, json)
    {
        private const long DefaultRequestLimit = 30_000_000;
        private readonly int memoryThreshold = json.InputFormatterMemoryBufferThreshold;

        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            // TextInputFormatter selected the supported encoding before this method.
            // Drain asynchronously before the stock reader performs synchronous I/O.
            var request = context.HttpContext.Request;
            var original = request.Body;
            var originalPosition = original.CanSeek ? original.Position : (long?)null;
            var activeLimit = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize;
            // Bounded adapter cap; this does not alter global server limits.
            var limit = Math.Min(activeLimit ?? DefaultRequestLimit, DefaultRequestLimit);
            await using var buffered = new FileBufferingReadStream(original, memoryThreshold, limit,
                static () => Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath());
            try
            {
                request.Body = buffered;
                await buffered.DrainAsync(context.HttpContext.RequestAborted);
                buffered.Position = 0;
                return await base.ReadRequestBodyAsync(context, encoding);
            }
            finally
            {
                request.Body = original;
                if (originalPosition.HasValue) original.Position = originalPosition.Value;
            }
        }
    }
}

internal sealed class OrderSourceBodyInputFormatter(NewtonsoftJsonInputFormatter source)
    : IInputFormatter, IInputFormatterExceptionPolicy
{
    // The inner stock formatter is subclassed only for owned buffering. Preserve
    // stock malformed-input exception policy at the actual selected boundary.
    public InputFormatterExceptionPolicy ExceptionPolicy => InputFormatterExceptionPolicy.MalformedInputExceptions;

    public bool CanRead(InputFormatterContext context) => SourceType(context.ModelType) is not null && source.CanRead(context);

    public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        var type = SourceType(context.ModelType)
            ?? throw new InvalidOperationException("The source body formatter received an unowned contract.");
        var metadata = new EmptyModelMetadataProvider().GetMetadataForType(type);
        var state = new ModelStateDictionary(context.ModelState.MaxAllowedErrors);
        var originalContext = new InputFormatterContext(context.HttpContext, context.ModelName, state, metadata,
            context.ReaderFactory, context.TreatEmptyInputAsDefaultValue);
        var result = await source.ReadAsync(originalContext);
        if (result.HasError)
        {
            // A path or message may contain a literal user-supplied property/value.
            // Keep unsupported-charset classification for MVC's existing 415 filter.
            if (state.Values.SelectMany(value => value.Errors).Any(error => error.Exception is UnsupportedContentTypeException))
                context.ModelState.TryAddModelError(context.ModelName, new UnsupportedContentTypeException("The request content type is unsupported."), context.Metadata);
            else
                context.ModelState.TryAddModelError(context.ModelName, "The request body contains an invalid value.");
            return InputFormatterResult.Failure();
        }
        if (!result.IsModelSet) return result;
        if (result.Model is null) return InputFormatterResult.Success(null);
        return InputFormatterResult.Success(Project(result.Model));
    }

    private static Type? SourceType(Type type)
    {
        if (type == typeof(UpsertCategoryRequest)) return typeof(SourceCategory);
        if (type == typeof(UpsertFileFormatRequest)) return typeof(SourceFormat);
        if (type == typeof(UpsertProcessRequest)) return typeof(SourceProcess);
        if (type == typeof(UpsertOrderStatusRequest)) return typeof(SourceStatus);
        if (type == typeof(UpsertOrderFileRequest)) return typeof(SourceFile);
        return type == typeof(UpsertOrderStatusHistoryRequest) ? typeof(SourceHistory) : null;
    }

    private static object Project(object body) => body switch
    {
        SourceCategory value => new UpsertCategoryRequest(value.Name),
        SourceFormat value => new UpsertFileFormatRequest(value.Name, value.Extension),
        SourceProcess value => new UpsertProcessRequest(value.CategoryId, value.Name),
        SourceStatus value => new UpsertOrderStatusRequest(value.Name, value.Description),
        // Always project the original integer, including zero. Only direct DTO
        // callers retain the nullable owner-fallback policy.
        SourceFile value => new UpsertOrderFileRequest(value.OrderId, value.Bucket, value.ObjectName),
        SourceHistory value => new UpsertOrderStatusHistoryRequest(value.OrderId, value.OrderStatusId),
        _ => throw new InvalidOperationException("The source body formatter produced an unowned model."),
    };
}
