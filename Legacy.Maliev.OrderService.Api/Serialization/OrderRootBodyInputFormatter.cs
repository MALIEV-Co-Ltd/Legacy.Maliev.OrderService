using System.Buffers;
using System.Runtime.ExceptionServices;
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
using Newtonsoft.Json.Serialization;

namespace Legacy.Maliev.OrderService.Api.Serialization;

/// <summary>Adds input-only source Order admission with a strict modern root operation key.</summary>
/// <param name="loggers">The application logger factory.</param>
/// <param name="pools">The application object pool provider.</param>
public sealed class OrderRootBodyInputFormatterSetup(ILoggerFactory loggers, ObjectPoolProvider pools) : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options)
    {
        if (options.InputFormatters.OfType<OrderRootBodyInputFormatter>().Any()) return;
        var json = new MvcNewtonsoftJsonOptions();
        json.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        json.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        // Retain stock MaxDepth32, TypeNameHandling.None, metadata handling,
        // supported encodings and CheckAdditionalContent=false. No output registration.
        options.InputFormatters.Insert(0, new OrderRootBodyInputFormatter(
            new SafeLogger(loggers.CreateLogger<OrderRootBodyInputFormatterSetup>()), json, pools, options));
    }

    private sealed class SafeLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            target.BeginScope("Order root source admission");
        public bool IsEnabled(LogLevel logLevel) => target.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (target.IsEnabled(logLevel))
                target.Log(logLevel, eventId, "OrderRootSourceAdmissionFailure");
        }
    }
}

internal sealed class OrderRootBodyInputFormatter(
    ILogger logger, MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, MvcOptions mvc)
    : IInputFormatter, IInputFormatterExceptionPolicy
{
    public InputFormatterExceptionPolicy ExceptionPolicy => InputFormatterExceptionPolicy.MalformedInputExceptions;

    public bool CanRead(InputFormatterContext context) =>
        context.ModelType == typeof(UpsertOrderRequest) && NewSource(new RequestCapture()).CanRead(context);

    public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        if (context.ModelType != typeof(UpsertOrderRequest))
            throw new InvalidOperationException("The root formatter received an unowned contract.");
        var capture = new RequestCapture();
        var state = new ModelStateDictionary(context.ModelState.MaxAllowedErrors);
        var sourceContext = new InputFormatterContext(context.HttpContext, context.ModelName, state,
            new EmptyModelMetadataProvider().GetMetadataForType(typeof(SourceRootOrder)),
            (stream, encoding) =>
            {
                if (capture.Reader is not null)
                    throw new InvalidOperationException("A root request may own only one decoded reader.");
                capture.Reader = new DecodedCapture(context.ReaderFactory(stream, encoding), capture,
                    context.HttpContext.RequestAborted);
                return capture.Reader;
            }, context.TreatEmptyInputAsDefaultValue);
        InputFormatterResult? result = null;
        Exception? primary = null;
        try { result = await NewSource(capture).ReadAsync(sourceContext); }
        catch (Exception failure) { primary = failure; }
        finally { capture.Reader?.Dispose(); }
        // Json.NET's ordinary error event can mark a reader cancellation handled.
        // A request-owned cancellation must not become a successful/400 admission.
        if (primary is null && capture.FirstSourceFailure is OperationCanceledException canceled)
            primary = canceled;
        if (primary is null && context.HttpContext.RequestAborted.IsCancellationRequested)
            primary = new OperationCanceledException(context.HttpContext.RequestAborted);
        if (capture.CleanupFailure is { } cleanup)
        {
            var originals = new List<Exception>();
            if (primary is not null) originals.Add(primary);
            else if (capture.FirstSourceFailure is { } original) originals.Add(original);
            originals.Add(cleanup);
            // Private exception custody only: never log or serialize originals.
            throw originals.Count == 1 ? originals[0] : new AggregateException(originals);
        }
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
        if (result!.HasError)
        {
            if (state.Values.SelectMany(value => value.Errors).Any(error => error.Exception is UnsupportedContentTypeException))
                context.ModelState.TryAddModelError(context.ModelName,
                    new UnsupportedContentTypeException("The request content type is unsupported."), context.Metadata);
            else if (capture.InvalidKey)
                context.ModelState.TryAddModelError("OperationKey", "The operation key must be a valid JSON string or null.");
            else
                context.ModelState.TryAddModelError(context.ModelName, "The request body contains an invalid value.");
            return InputFormatterResult.Failure();
        }
        if (!result.IsModelSet || result.Model is null) return result;
        var body = (SourceRootOrder)result.Model;
        // Project consumed scalars only. Navigation graphs/IDs/audits/computed
        // fields are parsed but never tracked, mapped or used for persistence.
        return InputFormatterResult.Success(new UpsertOrderRequest(body.CustomerId, body.EmployeeId,
            body.Name, body.Description, body.ProcessId, body.MaterialId, body.SurfaceFinishId, body.ColorId,
            body.Quantity, body.Manufactured, body.UnitPrice, body.DiscountPercent, body.CurrencyId, body.LeadTime,
            body.PromisedDate, body.FinishedDate, body.Comment, body.AllowSocialMedia, body.AllowCancellation,
            body.AllowPayment, body.TrackingNumber, body.OperationKey));
    }

    private BufferedRootFormatter NewSource(RequestCapture capture) => new(logger, json, pools, mvc, capture);

    private sealed class RequestCapture
    {
        internal DecodedCapture? Reader { get; set; }
        internal int MaximumCharacters { get; set; } = 30_000_000;
        internal bool InvalidKey { get; set; }
        // One original source failure is retained only for cleanup custody; no
        // receipt/exception serialization and no assertion of a complete error ledger.
        internal Exception? FirstSourceFailure { get; private set; }
        internal void ObserveSourceFailure(object? sender, Newtonsoft.Json.Serialization.ErrorEventArgs args) =>
            FirstSourceFailure ??= args.ErrorContext.Error;
        internal Exception? CleanupFailure { get; set; }
    }

    private sealed class BufferedRootFormatter(
        ILogger logger, MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, MvcOptions mvc, RequestCapture capture)
        : NewtonsoftJsonInputFormatter(logger, json.SerializerSettings, ArrayPool<char>.Shared, pools, mvc, json)
    {
        private readonly IContractResolver sourceResolver = json.SerializerSettings.ContractResolver ?? new DefaultContractResolver();

        protected override Newtonsoft.Json.JsonSerializer CreateJsonSerializer(InputFormatterContext context)
        {
            var serializer = Newtonsoft.Json.JsonSerializer.Create(SerializerSettings);
            serializer.ContractResolver = new RootResolver(sourceResolver, capture);
            serializer.Error += capture.ObserveSourceFailure;
            return serializer;
        }

        protected override void ReleaseJsonSerializer(Newtonsoft.Json.JsonSerializer serializer)
        {
            // This serializer was freshly created, never rented. Do not return it
            // to MVC's pool or retain a converter/contract referencing this request.
            serializer.Error -= capture.ObserveSourceFailure;
            serializer.ContractResolver = sourceResolver;
        }

        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            var request = context.HttpContext.Request;
            var original = request.Body;
            var position = original.CanSeek ? original.Position : (long?)null;
            long limit = Math.Min(context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize
                ?? 30_000_000, 30_000_000);
            capture.MaximumCharacters = checked((int)limit);
            var buffered = new FileBufferingReadStream(original, json.InputFormatterMemoryBufferThreshold, limit,
                static () => Environment.GetEnvironmentVariable("ASPNETCORE_TEMP") ?? Path.GetTempPath());
            InputFormatterResult? result = null;
            Exception? primary = null;
            var cleanup = new List<Exception>();
            try
            {
                request.Body = buffered;
                await buffered.DrainAsync(context.HttpContext.RequestAborted);
                buffered.Position = 0;
                result = await base.ReadRequestBodyAsync(context, encoding);
            }
            catch (Exception failure) { primary = failure; }
            finally
            {
                try { request.Body = original; }
                catch (Exception failure) { cleanup.Add(failure); }
                try { if (position.HasValue) original.Position = position.Value; }
                catch (Exception failure) { cleanup.Add(failure); }
                try { await buffered.DisposeAsync(); }
                catch (Exception failure) { cleanup.Add(failure); }
            }
            if (cleanup.Count > 0)
            {
                if (primary is not null) cleanup.Insert(0, primary);
                else if (capture.FirstSourceFailure is { } originalFailure) cleanup.Insert(0, originalFailure);
                throw cleanup.Count == 1 ? cleanup[0] : new AggregateException(cleanup);
            }
            if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
            return result!;
        }
    }

    private sealed class RootResolver(IContractResolver source, RequestCapture capture) : DefaultContractResolver
    {
        public override JsonContract ResolveContract(Type type) =>
            type == typeof(SourceRootOrder) ? base.ResolveContract(type) : source.ResolveContract(type);

        protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
        {
            var properties = base.CreateProperties(type, memberSerialization);
            var key = properties.Single(property => property.UnderlyingName == nameof(SourceRootOrder.OperationKey));
            key.Converter = new RootKeyConverter(capture);
            key.NullValueHandling = NullValueHandling.Include;
            return properties;
        }
    }

    private sealed class RootKeyConverter(RequestCapture capture) : JsonConverter<string?>
    {
        public override bool CanWrite => false;
        public override string? ReadJson(JsonReader reader, Type objectType, string? existingValue,
            bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
        {
            try
            {
                while (reader.TokenType == JsonToken.Comment)
                    if (!reader.Read()) throw new JsonSerializationException("A root key value is required.");
                if (reader.Depth != 1 || reader is not IJsonLineInfo line || !line.HasLineInfo()
                    || capture.Reader is null)
                    throw new JsonSerializationException("The root key requires its owned public reader position.");
                if (reader.TokenType == JsonToken.Null) return null;
                if (reader.TokenType is not (JsonToken.String or JsonToken.Date))
                    throw new JsonSerializationException("The root key must be a JSON string or null.");
                string raw = capture.Reader.QuotedTokenEndingAt(line.LineNumber, line.LinePosition);
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(raw);
                try
                {
                    // Decode this value token only. No body replay, source date
                    // normalization or inferred replacement of malformed UTF-16.
                    return System.Text.Json.JsonSerializer.Deserialize<string?>(bytes);
                }
                finally { Array.Clear(bytes); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure) when (failure is JsonException or System.Text.Json.JsonException
                or EncoderFallbackException or InvalidOperationException)
            {
                capture.InvalidKey = true;
                throw new JsonSerializationException("The operation key must be a valid JSON string or null.", failure);
            }
        }

        public override void WriteJson(JsonWriter writer, string? value, Newtonsoft.Json.JsonSerializer serializer) =>
            throw new NotSupportedException("The root key converter is input-only.");
    }

    private sealed class DecodedCapture(TextReader owned, RequestCapture capture, CancellationToken cancellation) : TextReader
    {
        private readonly StringBuilder text = new();
        private readonly List<int> starts = [0];
        private bool carriageReturn;
        private bool disposed;

        public override int Read(char[] buffer, int index, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellation.ThrowIfCancellationRequested();
            int remaining = capture.MaximumCharacters - text.Length;
            int read = owned.Read(buffer, index, Math.Min(count, remaining + 1));
            for (int i = index; i < index + read; i++)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (text.Length == capture.MaximumCharacters)
                    throw new IOException("The bounded decoded request limit was exceeded.");
                char value = buffer[i];
                text.Append(value);
                if (value == '\r') { starts.Add(text.Length); carriageReturn = true; }
                else if (value == '\n')
                {
                    if (carriageReturn) starts[^1] = text.Length;
                    else starts.Add(text.Length);
                    carriageReturn = false;
                }
                else carriageReturn = false;
            }
            return read;
        }

        internal string QuotedTokenEndingAt(int line, int column)
        {
            cancellation.ThrowIfCancellationRequested();
            if (line < 1 || line > starts.Count || column < 1)
                throw new InvalidOperationException("The key position is outside its decoded capture.");
            int end = checked(starts[line - 1] + column);
            if (end < 2 || end > text.Length || text[end - 1] is not ('"' or '\''))
                throw new InvalidOperationException("The key position does not end at its source quote.");
            char quote = text[end - 1];
            // Json.NET has already accepted this ONE quoted token. Its nearest
            // preceding unescaped matching quote is its opening delimiter.
            // Public end position is logical UTF-16, not the read-ahead buffer end.
            for (int i = end - 2; i >= 0; i--)
            {
                if ((i & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (text[i] != quote) continue;
                int preceding = i - 1;
                while (preceding >= 0 && text[preceding] == '\\')
                {
                    if ((preceding & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                    preceding--;
                }
                if (((i - preceding - 1) & 1) == 0)
                    return text.ToString(i, end - i);
            }
            throw new InvalidOperationException("The source key opening quote is absent.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed) return;
            disposed = true;
            try { if (disposing) owned.Dispose(); }
            catch (Exception failure) { capture.CleanupFailure = failure; }
            finally
            {
                text.Clear();
                starts.Clear();
                base.Dispose(disposing);
            }
        }
    }
}

// Full admitted source input graph, not an EF entity or output contract.
// Initial/72eb field types are unchanged in latest source; latest adds the
// Name100/Description250 annotations, validated on the projected current record.
internal sealed class SourceRootOrder : SourceOrderGraphOrder
{
    public string? OperationKey { get; set; }
}

// Both root and nested Orders share the same recursive legacy base identity.
// A root $id can therefore be assigned back through Process.Order or
// OrderFile.Order. Only the derived root contract has the modern key property.
internal class SourceOrderGraphOrder
{
    public int Id { get; set; }
    public int? CustomerId { get; set; }
    public int? EmployeeId { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int ProcessId { get; set; }
    public int? MaterialId { get; set; }
    public int? SurfaceFinishId { get; set; }
    public int? ColorId { get; set; }
    public int Quantity { get; set; }
    public int Manufactured { get; set; }
    public int? Remaining { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? DiscountPercent { get; set; }
    public decimal? Subtotal { get; set; }
    public int? CurrencyId { get; set; }
    public int? LeadTime { get; set; }
    public DateTime? PromisedDate { get; set; }
    public DateTime? FinishedDate { get; set; }
    public int? Turnaround { get; set; }
    public string? Comment { get; set; }
    public bool AllowSocialMedia { get; set; }
    public bool AllowCancellation { get; set; }
    public bool AllowPayment { get; set; }
    public string? TrackingNumber { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public SourceOrderGraphProcess? Process { get; set; }
    public ICollection<SourceOrderGraphFile> OrderFile { get; set; } = new HashSet<SourceOrderGraphFile>();
}

internal sealed class SourceOrderGraphProcess
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public string? Name { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public SourceOrderGraphCategory? Category { get; set; }
    public ICollection<SourceOrderGraphOrder> Order { get; set; } = new HashSet<SourceOrderGraphOrder>();
}

internal sealed class SourceOrderGraphCategory
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<SourceOrderGraphProcess> Process { get; set; } = new HashSet<SourceOrderGraphProcess>();
}

internal sealed class SourceOrderGraphFile
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public string? Bucket { get; set; }
    public string? ObjectName { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public SourceOrderGraphOrder? Order { get; set; }
}
