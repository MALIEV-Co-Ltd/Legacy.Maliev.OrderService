using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
using Legacy.Maliev.OrderService.Application.Models;
using Newtonsoft.Json;
using StjException = System.Text.Json.JsonException;

namespace Legacy.Maliev.OrderService.Tests.Serialization;

public sealed class OrderRootOperationKeyCaptureExperimentTests
{
    [Theory]
    [InlineData("OperationKey")]
    [InlineData("operationkey")]
    [InlineData(@"Oper\u0061tionKey")]
    [InlineData(@"\u004fperationKey")]
    public void Root_alias_uses_original_token_without_changing_legacy_graph(string property)
    {
        var receipt = new OrderRootCaptureReceipt();
        var value = Read("{'" + property + "':\"  2030-01-02T03:04:05Z  \",'Name':123,'Graph':{'OperationKey':456}}", receipt);
        Assert.Equal("  2030-01-02T03:04:05Z  ", value.OperationKey);
        Assert.Equal("123", value.Name);
        Assert.Equal("456", value.Graph!.OperationKey);
        Assert.Equal(1, receipt.RootKeysDecoded);
        Assert.Equal(1, receipt.TokenSpansDecoded);
        Assert.True(receipt.CaptureCleared);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(1024)]
    public void Refills_comments_and_CRLF_use_logical_positions_not_readahead(int chunk)
    {
        const string json = "{\r\n /*before*/ 'Name': 'ไทย',\r 'OperationKey':\n/* : { fake */ // second\r\n\t \"x\\\"y\\\\z\\uD83D\\uDE00\" , 'Graph': {'OperationKey':123}, 'Description':'after'}";
        var receipt = new OrderRootCaptureReceipt();
        var value = Read(json, receipt, chunk);
        Assert.Equal("x\"y\\z😀", value.OperationKey);
        Assert.Equal("ไทย", value.Name);
        Assert.Equal("after", value.Description);
        Assert.Equal("123", value.Graph!.OperationKey);
        Assert.Equal(1, receipt.RootKeysDecoded);
        Assert.Equal(json.Length, receipt.CharactersRead);
        Assert.True(receipt.ReaderDisposeReturned);
        Assert.True(receipt.CaptureCleared);
    }

    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8", true)]
    [InlineData("utf16le", false)]
    [InlineData("utf16le", true)]
    [InlineData("utf16be", false)]
    [InlineData("utf16be", true)]
    public void Decode_charset_then_capture_characters_once(string charset, bool bom)
    {
        Encoding encoding = charset switch
        {
            "utf16le" => new UnicodeEncoding(false, bom, true),
            "utf16be" => new UnicodeEncoding(true, bom, true),
            _ => new UTF8Encoding(bom, true),
        };
        const string text = "{\"Name\":\"ไทย\",\"OperationKey\":\"😀\\uFFFD\"}";
        byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var stream = new MemoryStream(bytes, writable: false);
        var owned = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 128, leaveOpen: false);
        var receipt = new OrderRootCaptureReceipt();
        var value = OrderRootOperationKeyCaptureExperiment.ReadOwned(owned, 4096, 3, receipt);
        Assert.Equal("😀�", value.OperationKey);
        Assert.Equal("ไทย", value.Name);
        Assert.False(stream.CanRead);
        Assert.True(receipt.ReaderDisposeReturned);
        Assert.Equal(text.Length, receipt.CharactersRead);
    }

    [Theory]
    [InlineData(@"{""OperationKey"":""\uFFFD""}", "�")]
    [InlineData(@"{""OperationKey"":""\uD83D\uDE00""}", "😀")]
    [InlineData(@"{""OperationKey"":""2030-01-02T03:04:05Z""}", "2030-01-02T03:04:05Z")]
    [InlineData(@"{""OperationKey"":""\/x\u003Ay""}", "/x:y")]
    public void Valid_native_strings_keep_the_exact_decoded_identity(string json, string expected)
    {
        Assert.Equal(expected, Read(json, new OrderRootCaptureReceipt()).OperationKey);
    }

    [Theory]
    [InlineData(@"{""OperationKey"":""\uD800""}")]
    [InlineData(@"{""OperationKey"":""\uDC00""}")]
    [InlineData(@"{""OperationKey"":""\uD800x""}")]
    [InlineData(@"{""OperationKey"":""\uD800\u0041""}")]
    [InlineData(@"{""OperationKey"":'single-quoted'}")]
    [InlineData("{\"OperationKey\":\"raw\nnewline\"}")]
    public void Malformed_or_non_native_key_string_is_not_normalized_into_a_usable_key(string json)
    {
        var receipt = new OrderRootCaptureReceipt();
        Exception? actual = Record.Exception(() => Read(json, receipt, 1));
        Assert.NotNull(actual);
        Assert.NotNull(receipt.PrimaryFailure);
        Assert.Same(receipt.PrimaryFailure, actual);
        Assert.Equal(0, receipt.RootKeysDecoded);
        Assert.True(receipt.ReaderDisposeReturned);
        Assert.True(receipt.CaptureCleared);
        Assert.True(ContainsNativeRejection(actual));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("undefined")]
    public void Key_kind_remains_native_string_or_null_only(string token)
    {
        var receipt = new OrderRootCaptureReceipt();
        Exception? actual = Record.Exception(() => Read("{\"OperationKey\":" + token + "}", receipt));
        Assert.NotNull(actual);
        Assert.True(ContainsNativeRejection(actual));
        Assert.Equal(0, receipt.RootKeysDecoded);
        Assert.True(receipt.CaptureCleared);
    }

    [Theory]
    [InlineData("{}", null, 0)]
    [InlineData(@"{""OperationKey"":null}", null, 1)]
    [InlineData(@"{""OperationKey"":""first"",""operationkey"":null}", null, 2)]
    [InlineData(@"{""OperationKey"":null,""OperationKey"":""last""}", "last", 2)]
    [InlineData(@"{""OperationKey"":""first"",""Oper\u0061tionKey"":""last""}", "last", 2)]
    public void Duplicate_key_null_is_not_legacy_null_ignore(string json, string? expected, int reads)
    {
        var receipt = new OrderRootCaptureReceipt();
        Assert.Equal(expected, Read(json, receipt).OperationKey);
        Assert.Equal(reads, receipt.RootKeysDecoded);
    }

    [Fact]
    public void Legacy_null_ignore_and_typed_nested_graph_are_still_owned_by_Newtonsoft()
    {
        var receipt = new OrderRootCaptureReceipt();
        var value = Read("{Name:'first',Name:null,LegacyDate:'2030-01-02T03:04:05Z',Graph:{OperationKey:123},OperationKey:null}", receipt);
        Assert.Equal("first", value.Name);
        Assert.NotNull(value.LegacyDate);
        Assert.Equal("123", value.Graph!.OperationKey);
        Assert.Null(value.OperationKey);
        Assert.Equal(1, receipt.RootKeysDecoded);
        Assert.Throws<JsonReaderException>(() => Read("{Graph:{Quantity:'invalid'}}", new OrderRootCaptureReceipt()));
    }

    [Theory]
    [InlineData("Name", 100, true)]
    [InlineData("Name", 101, false)]
    [InlineData("Description", 250, true)]
    [InlineData("Description", 251, false)]
    [InlineData("OperationKey", 128, true)]
    [InlineData("OperationKey", 129, false)]
    public void Existing_real_record_validation_metadata_is_not_replaced(string property, int length, bool accepted)
    {
        string text = new('x', length);
        var value = Read("{\"" + property + "\":\"" + text + "\"}", new OrderRootCaptureReceipt());
        var parameter = typeof(UpsertOrderRequest).GetConstructors().Single().GetParameters()
            .Single(item => item.Name == property);
        var attributes = parameter.GetCustomAttributes<ValidationAttribute>().ToArray();
        Assert.NotEmpty(attributes);
        var results = new List<ValidationResult>();
        Assert.Equal(accepted, Validator.TryValidateValue(
            typeof(OrderRootCaptureProbe).GetProperty(property)!.GetValue(value),
            new ValidationContext(value) { MemberName = property }, results, attributes));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void Key_length_uses_UTF16_and_does_not_count_unicode_scalars(int count, bool accepted)
    {
        string text = string.Concat(Enumerable.Repeat("😀", count));
        var value = Read("{\"OperationKey\":\"" + text + "\"}", new OrderRootCaptureReceipt());
        Assert.Equal(count * 2, value.OperationKey!.Length);
        var length = typeof(UpsertOrderRequest).GetConstructors().Single().GetParameters()
            .Single(parameter => parameter.Name == "OperationKey").GetCustomAttribute<StringLengthAttribute>()!;
        Assert.Equal(accepted, length.IsValid(value.OperationKey));
    }

    [Fact]
    public void Invalid_earlier_duplicate_is_not_hidden_by_a_later_valid_key()
    {
        var receipt = new OrderRootCaptureReceipt();
        Assert.NotNull(Record.Exception(() => Read(
            @"{""OperationKey"":""\uD800"",""OperationKey"":""valid""}", receipt)));
        Assert.Equal(0, receipt.RootKeysDecoded);
        Assert.True(receipt.CaptureCleared);
    }

    [Fact]
    public void Cap_refuses_after_at_most_one_extra_character_and_clears_owned_capture()
    {
        var owned = new ObservedReader(new string(' ', 1000) + "{}");
        var receipt = new OrderRootCaptureReceipt();
        Assert.Throws<OrderRootCaptureLimitException>(() =>
            OrderRootOperationKeyCaptureExperiment.ReadOwned(owned, 32, 7, receipt));
        Assert.Equal(33, owned.CharactersReturned);
        Assert.Equal(1, owned.Disposals);
        Assert.Equal(32, receipt.MaximumCharacters);
        Assert.True(receipt.CaptureCleared);
        Assert.Equal(0, receipt.RetainedCharacters);
    }

    [Fact]
    public void Reader_failure_and_cleanup_failure_retain_both_original_objects()
    {
        var primary = new IOException("controlled read failure");
        var cleanup = new IOException("controlled dispose failure");
        var owned = new ObservedReader("{}", primary, cleanup);
        var receipt = new OrderRootCaptureReceipt();
        var actual = Assert.Throws<IOException>(() =>
            OrderRootOperationKeyCaptureExperiment.ReadOwned(owned, 128, 7, receipt));
        Assert.Same(primary, actual);
        Assert.Same(primary, receipt.PrimaryFailure);
        Assert.Same(cleanup, Assert.Single(receipt.CleanupFailures));
        Assert.Equal(1, owned.Disposals);
        Assert.True(receipt.ReaderDisposeAttempted);
        Assert.False(receipt.ReaderDisposeReturned);
        Assert.True(receipt.CaptureCleared);
    }

    [Fact]
    public void Cleanup_failure_without_primary_is_not_reported_as_success()
    {
        var cleanup = new IOException("controlled dispose failure");
        var owned = new ObservedReader("{}", disposeFailure: cleanup);
        var receipt = new OrderRootCaptureReceipt();
        Assert.Same(cleanup, Assert.Throws<IOException>(() =>
            OrderRootOperationKeyCaptureExperiment.ReadOwned(owned, 128, 7, receipt)));
        Assert.Null(receipt.PrimaryFailure);
        Assert.Same(cleanup, Assert.Single(receipt.CleanupFailures));
        Assert.True(receipt.ReaderDisposeAttempted);
        Assert.False(receipt.ReaderDisposeReturned);
        Assert.True(receipt.CaptureCleared);
    }

    [Fact]
    public void Decoder_failure_still_disposes_the_owned_byte_stream()
    {
        var stream = new MemoryStream(new byte[] { 0xC3, 0x28 }, writable: false);
        var owned = new StreamReader(stream, new UTF8Encoding(false, true), false, 128, false);
        var receipt = new OrderRootCaptureReceipt();
        Assert.Throws<DecoderFallbackException>(() =>
            OrderRootOperationKeyCaptureExperiment.ReadOwned(owned, 128, 7, receipt));
        Assert.False(stream.CanRead);
        Assert.True(receipt.CaptureCleared);
    }

    private static OrderRootCaptureProbe Read(string json, OrderRootCaptureReceipt receipt, int chunk = 17)
        => OrderRootOperationKeyCaptureExperiment.ReadOwned(new StringReader(json), 16384, chunk, receipt);

    private static bool ContainsNativeRejection(Exception failure)
        => failure is StjException or EncoderFallbackException
            || failure.InnerException is { } inner && ContainsNativeRejection(inner);

    private sealed class ObservedReader(string text, Exception? readFailure = null, Exception? disposeFailure = null) : TextReader
    {
        private readonly StringReader inner = new(text);
        public int CharactersReturned { get; private set; }
        public int Disposals { get; private set; }
        public override int Read(char[] buffer, int index, int count)
        {
            if (readFailure is not null) throw readFailure;
            int read = inner.Read(buffer, index, count);
            CharactersReturned += read;
            return read;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
                inner.Dispose();
                if (disposeFailure is not null) throw disposeFailure;
            }
            base.Dispose(disposing);
        }
    }
}
