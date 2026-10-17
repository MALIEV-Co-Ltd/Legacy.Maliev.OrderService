using System.Buffers;
using System.Text;
using Legacy.Maliev.OrderService.Api.Serialization;
using Legacy.Maliev.OrderService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

namespace Legacy.Maliev.OrderService.Tests.Serialization;

public sealed class OrderSourceBodyBoundaryTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("\"OrderId\":null,", 0)]
    [InlineData("\"OrderId\":\"\",", 0)]
    [InlineData("\"OrderId\":21,\"OrderId\":null,", 21)]
    [InlineData("\"OrderId\":21,\"OrderId\":\"\",", 21)]
    public async Task StockSourceWitness_AndCandidate_RetainNativeOwnerPopulation(string owner, int expected)
    {
        var json = "{" + owner + "\"Bucket\":\"kept\",\"ObjectName\":\"kept.step\"}";
        var sourceContext = Context(typeof(NativeFile), json);
        var source = await Native().ReadAsync(sourceContext);
        Assert.False(source.HasError);
        Assert.Equal(expected, Assert.IsType<NativeFile>(source.Model).OrderId);
        var target = await Candidate().ReadAsync(Context(typeof(UpsertOrderFileRequest), json));
        Assert.False(target.HasError);
        var projected = Assert.IsType<UpsertOrderFileRequest>(target.Model);
        Assert.Equal((int?)expected, projected.OrderId);
        Assert.Equal("kept", projected.Bucket);
    }

    [Fact]
    public void ExactSixSelection_DoesNotReplaceOtherInputContracts()
    {
        var candidate = Candidate();
        foreach (var type in new[] { typeof(UpsertCategoryRequest), typeof(UpsertFileFormatRequest),
            typeof(UpsertProcessRequest), typeof(UpsertOrderStatusRequest), typeof(UpsertOrderFileRequest),
            typeof(UpsertOrderStatusHistoryRequest) })
            Assert.True(candidate.CanRead(Context(type, "{}")));
        foreach (var type in new[] { typeof(UpsertOrderRequest), typeof(OrderResponse), typeof(CategoryResponse), typeof(string) })
            Assert.False(candidate.CanRead(Context(type, "{}")));
        Assert.Equal(InputFormatterExceptionPolicy.MalformedInputExceptions,
            Assert.IsAssignableFrom<IInputFormatterExceptionPolicy>(candidate).ExceptionPolicy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("  ")]
    public async Task NativeNoValue_IsNotConvertedToAnEmptySuccessfulDto(string json)
    {
        var context = Context(typeof(UpsertCategoryRequest), json);
        var result = await Candidate().ReadAsync(context);
        Assert.False(result.HasError);
        Assert.False(result.IsModelSet);
        Assert.True(context.ModelState.IsValid);
    }

    [Fact]
    public async Task UnsupportedCharset_Is415ClassificationBeforeBodyRead()
    {
        var context = Context(typeof(UpsertCategoryRequest), "{\"Name\":\"kept\"}");
        using var stream = new AsyncOnlyBody(Encoding.UTF8.GetBytes("{\"Name\":\"kept\"}"));
        context.HttpContext.Request.Body = stream;
        context.HttpContext.Request.ContentType = "application/json; charset=iso-8859-1";
        var result = await Candidate().ReadAsync(context);
        Assert.True(result.HasError);
        var error = Assert.Single(context.ModelState.Values.SelectMany(value => value.Errors));
        Assert.IsType<UnsupportedContentTypeException>(error.Exception);
        Assert.Equal(0, stream.AsyncReads);
        Assert.Same(stream, context.HttpContext.Request.Body);
    }

    [Fact]
    public async Task NonSeekableBody_IsDrainedAsynchronously_AndOwnedSpillIsRemoved()
    {
        var context = Context(typeof(UpsertCategoryRequest), "");
        using var original = new AsyncOnlyBody(Encoding.UTF8.GetBytes("{\"Name\":\"" + new string('x', 80_000) + "\"}"));
        FileBufferingReadStream? owned = null;
        string? spill = null;
        original.OnRead = () =>
        {
            owned = context.HttpContext.Request.Body as FileBufferingReadStream;
            spill ??= owned?.TempFileName;
        };
        context.HttpContext.Request.Body = original;
        var result = await Candidate().ReadAsync(context);
        Assert.False(result.HasError);
        Assert.Equal(80_000, Assert.IsType<UpsertCategoryRequest>(result.Model).Name!.Length);
        Assert.True(original.AsyncReads > 0);
        Assert.Same(original, context.HttpContext.Request.Body);
        Assert.True(original.CanRead);
        Assert.NotNull(owned);
        Assert.NotNull(spill);
        Assert.False(File.Exists(spill));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => { await owned.ReadAsync(new byte[1]); });
    }

    [Fact]
    public async Task ActiveSmallerLimit_PreservesIOExceptionAndRestoresSeekableBody()
    {
        var context = Context(typeof(UpsertCategoryRequest), "{\"Name\":\"private-limit-marker\"}");
        var original = context.HttpContext.Request.Body;
        context.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(new BodyLimit(8));
        await Assert.ThrowsAsync<IOException>(() => Candidate().ReadAsync(context));
        Assert.Same(original, context.HttpContext.Request.Body);
        Assert.Equal(0, original.Position);
        Assert.True(original.CanRead);
        Assert.True(context.ModelState.IsValid);
    }

    [Fact]
    public async Task Cancellation_IsNotTurnedIntoMalformedInput()
    {
        var context = Context(typeof(UpsertCategoryRequest), "");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.HttpContext.RequestAborted = cancellation.Token;
        using var original = new AsyncOnlyBody(Encoding.UTF8.GetBytes("{\"Name\":\"kept\"}"));
        context.HttpContext.Request.Body = original;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Candidate().ReadAsync(context));
        Assert.Same(original, context.HttpContext.Request.Body);
        Assert.True(context.ModelState.IsValid);
    }

    [Fact]
    public async Task MalformedKnownField_SanitizesModelStateAndEveryFormatterLog()
    {
        using var logs = new RecordingLoggerFactory();
        var context = Context(typeof(UpsertCategoryRequest), "{\"Name\":\"private-value-marker\",\"Id\":{\"private-path-marker\":true}}");
        var result = await Candidate(logs).ReadAsync(context);
        Assert.True(result.HasError);
        Assert.NotEmpty(context.ModelState.Values.SelectMany(value => value.Errors));
        Assert.All(context.ModelState.Values.SelectMany(value => value.Errors), error =>
        {
            Assert.Equal("The request body contains an invalid value.", error.ErrorMessage);
            Assert.Null(error.Exception);
        });
        Assert.NotEmpty(logs.Messages);
        Assert.All(logs.Messages, message =>
        {
            Assert.DoesNotContain("private-value-marker", message, StringComparison.Ordinal);
            Assert.DoesNotContain("private-path-marker", message, StringComparison.Ordinal);
        });
        Assert.All(logs.Exceptions, exception => Assert.Null(exception));
    }

    private static IInputFormatter Candidate(ILoggerFactory? logs = null)
    {
        var mvc = new MvcOptions();
        new OrderSourceBodyInputFormatterSetup(logs ?? NullLoggerFactory.Instance, new DefaultObjectPoolProvider()).Configure(mvc);
        return Assert.Single(mvc.InputFormatters);
    }
    private static NewtonsoftJsonInputFormatter Native()
    {
        var options = new MvcNewtonsoftJsonOptions();
        options.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        options.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        return new NewtonsoftJsonInputFormatter(NullLogger.Instance, options.SerializerSettings,
            ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), options);
    }
    private static InputFormatterContext Context(Type type, string json)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return new InputFormatterContext(http, "item", new ModelStateDictionary(),
            new EmptyModelMetadataProvider().GetMetadataForType(type),
            (stream, encoding) => new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true));
    }
    // Independent original OrderFile scalar witness; source OrderId is int, not the nullable target DTO.
    private sealed class NativeFile
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public string? Bucket { get; set; }
        public string? ObjectName { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }
    private sealed class BodyLimit(long limit) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = limit;
    }
    private sealed class AsyncOnlyBody(byte[] bytes) : Stream
    {
        private readonly MemoryStream source = new(bytes);
        public int AsyncReads { get; private set; }
        public Action? OnRead { get; set; }
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Synchronous body read.");
        public override int Read(Span<byte> buffer) => throw new InvalidOperationException("Synchronous body read.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AsyncReads++;
            OnRead?.Invoke();
            return source.ReadAsync(buffer, cancellationToken);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<string> Messages { get; } = [];
        public List<Exception?> Exceptions { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        private sealed class Recorder(RecordingLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                owner.Messages.Add(formatter(state, exception));
                owner.Exceptions.Add(exception);
            }
        }
    }
}
