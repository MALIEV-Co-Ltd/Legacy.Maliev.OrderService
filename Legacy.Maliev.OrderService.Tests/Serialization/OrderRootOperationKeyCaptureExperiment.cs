using System.Runtime.ExceptionServices;
using System.Text;
using Newtonsoft.Json;

namespace Legacy.Maliev.OrderService.Tests.Serialization;

// An inert experiment only: no MVC registration, request-body replay, or production graph projection.
internal static class OrderRootOperationKeyCaptureExperiment
{
    internal static OrderRootCaptureProbe ReadOwned(TextReader owned, int maximumCharacters,
        int maximumReadChunk, OrderRootCaptureReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(receipt);
        CapturingReader? capture = null;
        PositionedReader? reader = null;
        try
        {
            capture = new CapturingReader(owned, maximumCharacters, maximumReadChunk, receipt);
            reader = new PositionedReader(capture, receipt) { CloseInput = false };
            var serializer = Newtonsoft.Json.JsonSerializer.Create(new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                TypeNameHandling = TypeNameHandling.None,
                CheckAdditionalContent = true,
            });
            return serializer.Deserialize<OrderRootCaptureProbe>(reader)
                ?? throw new JsonSerializationException("The experiment requires one non-null root object.");
        }
        catch (Exception failure)
        {
            receipt.PrimaryFailure = failure;
            throw;
        }
        finally
        {
            try { reader?.Close(); }
            catch (Exception failure) { receipt.AddCleanupFailure(failure); }
            try
            {
                if (capture is null) owned.Dispose();
                else capture.Dispose();
            }
            catch (Exception failure) { receipt.AddCleanupFailure(failure); }
            if (receipt.PrimaryFailure is null && receipt.CleanupFailures.Count > 0)
            {
                Exception failure = receipt.CleanupFailures.Count == 1
                    ? receipt.CleanupFailures[0] : new AggregateException(receipt.CleanupFailures);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private sealed class CapturingReader : TextReader
    {
        private readonly TextReader owned;
        private readonly int maximumCharacters;
        private readonly int maximumReadChunk;
        private readonly OrderRootCaptureReceipt receipt;
        private readonly StringBuilder text = new();
        private readonly List<int> lineStarts = [0];
        private bool precedingCarriageReturn;
        private bool disposed;

        internal CapturingReader(TextReader owned, int maximumCharacters, int maximumReadChunk,
            OrderRootCaptureReceipt receipt)
        {
            if (maximumCharacters is < 1 or > 1_048_576)
                throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
            if (maximumReadChunk is < 1 or > 4096)
                throw new ArgumentOutOfRangeException(nameof(maximumReadChunk));
            this.owned = owned;
            this.maximumCharacters = maximumCharacters;
            this.maximumReadChunk = maximumReadChunk;
            this.receipt = receipt;
            receipt.MaximumCharacters = maximumCharacters;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (index > buffer.Length - count) throw new ArgumentException("Invalid buffer range.");
            if (count == 0) return 0;
            int allowed = Math.Min(count, Math.Min(maximumReadChunk, maximumCharacters - text.Length + 1));
            int read = owned.Read(buffer, index, allowed);
            for (int i = index; i < index + read; i++)
            {
                receipt.CharactersRead++;
                if (text.Length == maximumCharacters) throw new OrderRootCaptureLimitException();
                char value = buffer[i];
                text.Append(value);
                if (value == '\r')
                {
                    lineStarts.Add(text.Length);
                    precedingCarriageReturn = true;
                }
                else if (value == '\n')
                {
                    if (precedingCarriageReturn) lineStarts[^1] = text.Length;
                    else lineStarts.Add(text.Length);
                    precedingCarriageReturn = false;
                }
                else precedingCarriageReturn = false;
            }
            receipt.RetainedCharacters = text.Length;
            return read;
        }

        internal int Offset(int line, int column)
        {
            if (line < 1 || line > lineStarts.Count || column < 0)
                throw new InvalidOperationException("The public reader position is outside the owned capture.");
            int offset = checked(lineStarts[line - 1] + column);
            if (offset < 0 || offset > text.Length)
                throw new InvalidOperationException("The public reader position is outside the owned capture.");
            return offset;
        }

        internal string Token(int start, int end)
        {
            if (start < 0 || end < start || end > text.Length)
                throw new InvalidOperationException("The key token span is outside the owned capture.");
            // Newtonsoft already consumed the colon and emitted every intervening comment.
            // Only its accepted whitespace remains before this one value token.
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            return text.ToString(start, end - start);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed) return;
            disposed = true;
            receipt.ReaderDisposeAttempted = disposing;
            try
            {
                if (disposing)
                {
                    owned.Dispose();
                    receipt.ReaderDisposeReturned = true;
                }
            }
            finally
            {
                text.Clear();
                lineStarts.Clear();
                receipt.CaptureCleared = true;
                receipt.RetainedCharacters = 0;
                base.Dispose(disposing);
            }
        }
    }

    private sealed class PositionedReader(CapturingReader capture, OrderRootCaptureReceipt receipt)
        : JsonTextReader(capture)
    {
        private int? pendingStart;
        private string? currentToken;
        private bool hasCurrentToken;

        public override bool Read()
        {
            while (true)
            {
                bool next = base.Read();
                if (!next) return false;
                if (pendingStart is not null)
                {
                    int end = capture.Offset(LineNumber, LinePosition);
                    if (TokenType == JsonToken.Comment)
                    {
                        // A property converter makes ReadForType call Read(), not
                        // ReadAndMoveToContent(). Consume only this key's trivia here.
                        pendingStart = end;
                        continue;
                    }
                    currentToken = capture.Token(pendingStart.Value, end);
                    hasCurrentToken = true;
                    pendingStart = null;
                }
                if (TokenType == JsonToken.PropertyName && Depth == 1
                    && Value is string name && string.Equals(name, "OperationKey", StringComparison.OrdinalIgnoreCase))
                {
                    pendingStart = capture.Offset(LineNumber, LinePosition);
                }
                return true;
            }
        }

        internal string? DecodeCurrentKey()
        {
            if (!hasCurrentToken || currentToken is null || Depth != 1)
                throw new InvalidOperationException("The root key converter requires its own current token.");
            string raw = currentToken;
            currentToken = null;
            hasCurrentToken = false;
            receipt.TokenSpansDecoded++;
            // No date conversion, normalization, reconstruction, or whole-document STJ replay.
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(raw);
            try
            {
                string? value = System.Text.Json.JsonSerializer.Deserialize<string?>(bytes);
                receipt.RootKeysDecoded++;
                return value;
            }
            finally { Array.Clear(bytes); }
        }
    }

    internal sealed class RootKeyConverter : JsonConverter<string?>
    {
        public RootKeyConverter() { }
        public override bool CanWrite => false;
        public override string? ReadJson(JsonReader reader, Type objectType, string? existingValue,
            bool hasExistingValue, Newtonsoft.Json.JsonSerializer serializer)
            => reader is PositionedReader positioned
                ? positioned.DecodeCurrentKey()
                : throw new InvalidOperationException("The experiment requires its owned positioned reader.");

        public override void WriteJson(JsonWriter writer, string? value, Newtonsoft.Json.JsonSerializer serializer)
            => throw new NotSupportedException("The input-only experiment does not replace output serialization.");
    }
}

internal sealed class OrderRootCaptureProbe
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public DateTime? LegacyDate { get; set; }
    public OrderRootCaptureGraph? Graph { get; set; }
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    [JsonConverter(typeof(OrderRootOperationKeyCaptureExperiment.RootKeyConverter))]
    public string? OperationKey { get; set; }
}

internal sealed class OrderRootCaptureGraph
{
    public string? OperationKey { get; set; }
    public int Quantity { get; set; }
}

internal sealed class OrderRootCaptureReceipt
{
    internal int MaximumCharacters { get; set; }
    internal int CharactersRead { get; set; }
    internal int RetainedCharacters { get; set; }
    internal int RootKeysDecoded { get; set; }
    internal int TokenSpansDecoded { get; set; }
    internal bool ReaderDisposeAttempted { get; set; }
    internal bool ReaderDisposeReturned { get; set; }
    internal bool CaptureCleared { get; set; }
    // Exception references are internal test custody, never emitted or serialized as diagnostics.
    internal Exception? PrimaryFailure { get; set; }
    internal IReadOnlyList<Exception> CleanupFailures => cleanupFailures;
    private readonly List<Exception> cleanupFailures = [];
    internal void AddCleanupFailure(Exception failure) => cleanupFailures.Add(failure);
}

internal sealed class OrderRootCaptureLimitException : Exception
{
    internal OrderRootCaptureLimitException() : base("The bounded decoded capture limit was exceeded.") { }
}
