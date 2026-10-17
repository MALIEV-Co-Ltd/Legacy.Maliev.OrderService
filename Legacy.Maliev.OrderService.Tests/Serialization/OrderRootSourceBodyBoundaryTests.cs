using System.Buffers;
using System.Text;
using Legacy.Maliev.OrderService.Api.Serialization;
using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;

namespace Legacy.Maliev.OrderService.Tests.Serialization;

public sealed class OrderRootSourceBodyBoundaryTests
{
    [Theory]
    [InlineData("{OperationKey:\"literal\"}", "literal")]
    [InlineData("{'OperationKey':\"2026-10-16T00:00:00Z\"}", "2026-10-16T00:00:00Z")]
    [InlineData("{\"oper\\u0061tionKEY\":\"  ไทย 😀  \"}", "  ไทย 😀  ")]
    [InlineData("{OperationKey:/* comment */\"a\\\"b\\\\c\"}", "a\"b\\c")]
    [InlineData("{\r\nOperationKey:\r\n\"a\\r\\nb\"\r\n}", "a\r\nb")]
    [InlineData("{OperationKey:\"first\",operationkey:null}", null)]
    [InlineData("{OperationKey:null,OPERATIONKEY:\"last\"}", "last")]
    [InlineData("{OperationKey:\"�\"}", "�")]
    [InlineData("{OperationKey:\"\\uD83D\\uDE00\"}", "😀")]
    [InlineData("{/* \\\" ignored */OperationKey:\"\"}", "")]
    public async Task KeyLexeme_IsNativeStringOrNullWithoutSourceNormalization(string body, string? expected)
    {
        var context = Context(typeof(UpsertOrderRequest), body);
        var result = await Candidate().ReadAsync(context);
        Assert.False(result.HasError);
        Assert.Equal(expected, Assert.IsType<UpsertOrderRequest>(result.Model).OperationKey);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("undefined")]
    [InlineData("'single'")]
    [InlineData("\"\\uD800\"")]
    [InlineData("\"\\uDC00\"")]
    [InlineData("\"\\uD800x\"")]
    public async Task KeyKindOrMalformedUtf16_IsRejectedWithoutEcho(string token)
    {
        var context = Context(typeof(UpsertOrderRequest), "{OperationKey:" + token + "}");
        var result = await Candidate().ReadAsync(context);
        Assert.True(result.HasError);
        var error = Assert.Single(context.ModelState["OperationKey"]!.Errors);
        Assert.Equal("The operation key must be a valid JSON string or null.", error.ErrorMessage);
        Assert.Null(error.Exception);
    }

    [Fact]
    public async Task ReadAheadRefillsAndEscapedQuotes_DoNotCaptureAdjacentOrNestedValues()
    {
        var body = "{Unknown:'" + new string('x', 8193) +
            "',Process:{Name:'legacy',Order:[{OperationKey:'nested ignored'}]}," +
            "\"OperationKey\":\"a\\\\\\\"b\\\\\\\\c\",Name:123}";
        var context = Context(typeof(UpsertOrderRequest), body, chunk: 1);
        var result = await Candidate().ReadAsync(context);
        Assert.False(result.HasError);
        var value = Assert.IsType<UpsertOrderRequest>(result.Model);
        Assert.Equal("a\\\"b\\\\c", value.OperationKey);
        Assert.Equal("123", value.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StockCharsetSelectionAndInjectedBomAwareReader_ArePreserved(bool unicode)
    {
        var encoding = unicode ? Encoding.Unicode : Encoding.UTF8;
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(
            "{Name:'legacy',OperationKey:\"ไทย\"} /* retained trailing trivia */")).ToArray();
        var context = Context(typeof(UpsertOrderRequest), "", bytes: bytes);
        context.HttpContext.Request.ContentType = "application/json; charset=" + (unicode ? "utf-16" : "utf-8");
        var result = await Candidate().ReadAsync(context);
        Assert.False(result.HasError);
        Assert.Equal("ไทย", Assert.IsType<UpsertOrderRequest>(result.Model).OperationKey);
    }

    [Fact]
    public async Task UnknownTypedGraphs_AreReadAndValidatedButNeverProjectedOrWritten()
    {
        var good = Context(typeof(UpsertOrderRequest),
            "{Id:999,Name:123,Name:null,Process:{Category:{Name:1},Order:[{OrderFile:[{Order:{Quantity:2}}]}]},OrderFile:[],OperationKey:null}");
        var result = await Candidate().ReadAsync(good);
        Assert.False(result.HasError);
        var value = Assert.IsType<UpsertOrderRequest>(result.Model);
        Assert.Equal("123", value.Name);
        Assert.Null(value.OperationKey);
        Assert.Equal(0, value.ProcessId);
        var bad = Context(typeof(UpsertOrderRequest), "{Process:{Order:[{Quantity:{}}]},OperationKey:\"kept\"}");
        Assert.True((await Candidate().ReadAsync(bad)).HasError);
    }

    [Fact]
    public async Task SetupAndReadSelection_AreRootOnlyAndDoNotPoolRequestCapture()
    {
        var formatter = Candidate();
        Assert.True(formatter.CanRead(Context(typeof(UpsertOrderRequest), "{}")));
        foreach (var type in new[] { typeof(UpsertCategoryRequest), typeof(UpsertOrderFileRequest), typeof(OrderResponse) })
            Assert.False(formatter.CanRead(Context(type, "{}")));
        var first = Context(typeof(UpsertOrderRequest), "{OperationKey:\"first\"}");
        var second = Context(typeof(UpsertOrderRequest), "{OperationKey:null}");
        Assert.Equal("first", Assert.IsType<UpsertOrderRequest>((await formatter.ReadAsync(first)).Model).OperationKey);
        Assert.Null(Assert.IsType<UpsertOrderRequest>((await formatter.ReadAsync(second)).Model).OperationKey);
        var mvc = new MvcOptions();
        var setup = new OrderRootBodyInputFormatterSetup(NullLoggerFactory.Instance, new DefaultObjectPoolProvider());
        setup.Configure(mvc);
        setup.Configure(mvc);
        Assert.Single(mvc.InputFormatters);
    }

    [Fact]
    public async Task BodyCapCancellationAndReaderDisposal_KeepBorrowedBodyAndSafeErrors()
    {
        var context = Context(typeof(UpsertOrderRequest), "{OperationKey:\"bounded\"}");
        var body = context.HttpContext.Request.Body;
        context.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit(4));
        await Assert.ThrowsAsync<IOException>(() => Candidate().ReadAsync(context));
        Assert.Same(body, context.HttpContext.Request.Body);
        Assert.Equal(0, body.Position);
        Assert.True(body.CanRead);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var cancellation = Context(typeof(UpsertOrderRequest), "{}");
        cancellation.HttpContext.RequestAborted = canceled.Token;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Candidate().ReadAsync(cancellation));
        int disposed = 0;
        var owned = Context(typeof(UpsertOrderRequest), "{OperationKey:\"kept\"}", onDispose: () => disposed++);
        Assert.False((await Candidate().ReadAsync(owned)).HasError);
        Assert.Equal(1, disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderCleanupFailure_KeepsOriginalParseFailureReferenceWhenPresent(bool malformed)
    {
        var cleanup = new IOException("synthetic owned reader disposal");
        var originalContext = Context(typeof(UpsertOrderRequest), malformed ? "{ProcessId:{}}" : "{}");
        var context = new InputFormatterContext(originalContext.HttpContext, "item", new ModelStateDictionary(),
            originalContext.Metadata, (stream, encoding) =>
                new DisposalFailureReader(new StreamReader(stream, encoding, leaveOpen: true), cleanup));
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => Candidate().ReadAsync(context));
        if (!malformed) Assert.Same(cleanup, failure);
        else
        {
            var aggregate = Assert.IsType<AggregateException>(failure);
            Assert.Contains(aggregate.InnerExceptions, value => ReferenceEquals(value, cleanup));
            Assert.Contains(aggregate.InnerExceptions, value => value is Newtonsoft.Json.JsonException);
        }
        Assert.True(originalContext.HttpContext.Request.Body.CanRead);
    }

    private sealed class DisposalFailureReader(TextReader inner, Exception failure) : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => inner.Read(buffer, index, count);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); throw failure; }
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData("{Name:true,Description:123,Process:{Category:{Name:'nested'},Order:[{OrderFile:[{Order:{Quantity:2}}]}]}}")]
    [InlineData("{Id:'37',Quantity:'2',Quantity:null,Name:'first',Name:null}")]
    [InlineData("{PromisedDate:'2026-10-16T00:00:00Z',Comment:123,TrackingNumber:true,AllowPayment:'true'}")]
    [InlineData("{/* source grammar */Name:'legacy',} /* trailing */ []")]
    [InlineData("{Ignored:{Quantity:{},OperationKey:'ignored'},OrderFile:[]}")]
    [InlineData("{Process:[]}")]
    [InlineData("{OrderFile:[{Order:{Quantity:{}}}]}")]
    [InlineData("{UnitPrice:'not decimal'}")]
    [InlineData("{Name:{}}")]
    public async Task LegacyGrammarAndTypedGraphs_MatchIndependentStockMvcWitness(string body)
    {
        // Independent witness fields below are transcribed from the admitted
        // original Order/Process/Category/OrderFile shapes, not candidate types.
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var native = new NewtonsoftJsonInputFormatter(NullLogger.Instance, options.SerializerSettings,
            ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), options);
        var expected = await native.ReadAsync(Context(typeof(NativeOrder), body));
        var actual = await Candidate().ReadAsync(Context(typeof(UpsertOrderRequest), body));
        Assert.Equal(expected.HasError, actual.HasError);
        Assert.Equal(expected.IsModelSet, actual.IsModelSet);
        if (expected.HasError || !expected.IsModelSet) return;
        var source = Assert.IsType<NativeOrder>(expected.Model);
        var projected = Assert.IsType<UpsertOrderRequest>(actual.Model);
        Assert.Equal(source.Name, projected.Name);
        Assert.Equal(source.Description, projected.Description);
        Assert.Equal(source.ProcessId, projected.ProcessId);
        Assert.Equal(source.Quantity, projected.Quantity);
        Assert.Equal(source.PromisedDate, projected.PromisedDate);
        Assert.Equal(source.Comment, projected.Comment);
        Assert.Equal(source.TrackingNumber, projected.TrackingNumber);
        Assert.Equal(source.AllowPayment, projected.AllowPayment);
        Assert.Null(projected.OperationKey);
    }

    private sealed class NativeOrder
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
        public NativeProcess? Process { get; set; }
        public ICollection<NativeFile> OrderFile { get; set; } = new HashSet<NativeFile>();
    }

    private sealed class NativeProcess
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string? Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public NativeCategory? Category { get; set; }
        public ICollection<NativeOrder> Order { get; set; } = new HashSet<NativeOrder>();
    }

    private sealed class NativeCategory
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public ICollection<NativeProcess> Process { get; set; } = new HashSet<NativeProcess>();
    }

    private sealed class NativeFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string? Bucket { get; set; }
        public string? ObjectName { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public NativeOrder? Order { get; set; }
    }

    [Fact]
    public async Task CancellationRaisedInsideStockReader_IsNotSwallowedBySourceErrorHandling()
    {
        using var owner = new CancellationTokenSource();
        var original = new OperationCanceledException(owner.Token);
        var baseline = Context(typeof(UpsertOrderRequest), "{}");
        baseline.HttpContext.RequestAborted = owner.Token;
        int disposal = 0;
        var context = new InputFormatterContext(baseline.HttpContext, "item", new ModelStateDictionary(),
            baseline.Metadata, (_, _) => new FailingReader(() => { owner.Cancel(); throw original; }, () => disposal++));
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Candidate().ReadAsync(context));
        Assert.Same(original, actual);
        Assert.Equal(owner.Token, actual.CancellationToken);
        Assert.Equal(1, disposal);
    }

    private sealed class FailingReader(Func<int> read, Action dispose) : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => read();
        protected override void Dispose(bool disposing)
        {
            if (disposing) dispose();
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task SourceAndCleanupFaults_RetainBothOriginalObjectsWithoutDiagnosticAttachments()
    {
        var original = new Newtonsoft.Json.JsonSerializationException("synthetic source failure");
        var cleanup = new IOException("synthetic cleanup failure");
        var baseline = Context(typeof(UpsertOrderRequest), "{}");
        var context = new InputFormatterContext(baseline.HttpContext, "item", new ModelStateDictionary(),
            baseline.Metadata, (_, _) => new FailingReader(() => throw original, () => throw cleanup));
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => Candidate().ReadAsync(context));
        Assert.Collection(aggregate.InnerExceptions,
            failure => Assert.Same(original, failure), failure => Assert.Same(cleanup, failure));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalRootReferenceCycles_AreAssignableAndRetainRootOnlyKeyRules(bool fileCycle)
    {
        string graph = fileCycle
            ? "OrderFile:[{Order:{$ref:'root'}},{Order:{Name:'nested',OperationKey:123}}]"
            : "Process:{Order:[{$ref:'root'},{Name:'nested',OperationKey:123}]}";
        string body = "{$id:'root',Name:'cycle',Quantity:2,OperationKey:\"exact-root\"," + graph + "}";
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore;
        var stock = new NewtonsoftJsonInputFormatter(NullLogger.Instance, options.SerializerSettings,
            ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), options);
        var originalResult = await stock.ReadAsync(Context(typeof(NativeOrder), body));
        Assert.False(originalResult.HasError);
        var original = Assert.IsType<NativeOrder>(originalResult.Model);
        if (fileCycle) Assert.Same(original, original.OrderFile.First().Order);
        else Assert.Same(original, original.Process!.Order.First());
        var result = await Candidate().ReadAsync(Context(typeof(UpsertOrderRequest), body));
        Assert.False(result.HasError);
        var projected = Assert.IsType<UpsertOrderRequest>(result.Model);
        Assert.Equal(original.Name, projected.Name);
        Assert.Equal(original.Quantity, projected.Quantity);
        Assert.Equal("exact-root", projected.OperationKey);
        // A root key with the same wrong kind remains rejected; nested ordinary
        // Order.OperationKey is unknown source data, not the modern extension.
        var wrong = Context(typeof(UpsertOrderRequest), body.Replace("OperationKey:\"exact-root\"", "OperationKey:123", StringComparison.Ordinal));
        Assert.True((await Candidate().ReadAsync(wrong)).HasError);
        Assert.Contains("OperationKey", wrong.ModelState.Keys);
    }

    private static IInputFormatter Candidate()
    {
        var mvc = new MvcOptions();
        new OrderRootBodyInputFormatterSetup(NullLoggerFactory.Instance, new DefaultObjectPoolProvider()).Configure(mvc);
        return Assert.Single(mvc.InputFormatters);
    }

    private static InputFormatterContext Context(Type type, string text, int chunk = int.MaxValue,
        byte[]? bytes = null, Action? onDispose = null)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(bytes ?? Encoding.UTF8.GetBytes(text));
        return new InputFormatterContext(http, "item", new ModelStateDictionary(),
            new EmptyModelMetadataProvider().GetMetadataForType(type),
            (stream, encoding) => new ChunkReader(new StreamReader(stream, encoding,
                detectEncodingFromByteOrderMarks: true, leaveOpen: true), chunk, onDispose));
    }

    private sealed class ChunkReader(TextReader inner, int chunk, Action? onDispose) : TextReader
    {
        public override int Read(char[] buffer, int index, int count) => inner.Read(buffer, index, Math.Min(chunk, count));
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); onDispose?.Invoke(); }
            base.Dispose(disposing);
        }
    }

    private sealed class BodyLimit(long limit) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = limit;
    }
}
