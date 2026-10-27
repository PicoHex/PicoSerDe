namespace PicoJetson.Tests;

/// <summary>
/// Behavioural guard rails for the string/property-name token scan (P0-3).
/// Every expectation here is the pre-optimization behaviour: these tests are
/// written to pass unchanged on the byte-at-a-time scanner, so they pin "the
/// vectorized fast path produces the same tokens, the same bytes and the same
/// errors" rather than any new behaviour.
/// </summary>
public class JsonStringScanTests
{
    // ── helpers ──

    private static string Json(string s) => "\"" + s + "\"";

    private static string ReadStringError(byte[] json)
    {
        var r = new JsonReader(json);
        try
        {
            r.Read();
            return "<no exception>";
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    private static string ReadStringValue(byte[] json)
    {
        var r = new JsonReader(json);
        if (!r.Read())
            return "<no token>";
        if (r.TokenType != TokenType.String)
            return "<token " + r.TokenType + ">";
        return Encoding.UTF8.GetString(r.GetStringRaw());
    }

    private static byte[] ReadStringBytes(byte[] json)
    {
        var r = new JsonReader(json);
        if (!r.Read())
            return [];
        return r.GetStringRaw().ToArray();
    }

    private sealed class Seg : System.Buffers.ReadOnlySequenceSegment<byte>
    {
        public Seg(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public void Append(Seg next) => Next = next;
    }

    private static ReadOnlySequence<byte> Chunk(byte[] data, int chunk)
    {
        if (data.Length == 0)
            return ReadOnlySequence<byte>.Empty;
        Seg? first = null;
        Seg? last = null;
        long running = 0;
        for (var i = 0; i < data.Length; i += chunk)
        {
            var len = Math.Min(chunk, data.Length - i);
            var seg = new Seg(new ReadOnlyMemory<byte>(data, i, len), running);
            if (first is null)
                first = seg;
            else
                last!.Append(seg);
            last = seg;
            running += len;
        }
        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    /// <summary>
    /// Reads every token of the document and returns a comparable transcript
    /// (token type + the raw UTF-8 bytes of the value). Used to prove the
    /// sequence-mode reader agrees with the span-mode reader.
    /// </summary>
    private static List<string> TokenTranscript(ReadOnlySequence<byte> seq)
    {
        var list = new List<string>();
        var r = new JsonReader(seq, maxDepth: 256, isFinalBlock: true);
        while (r.Read())
            list.Add(Describe(r.TokenType, r.GetStringRaw()));
        r.Dispose();
        return list;
    }

    private static List<string> TokenTranscript(byte[] json)
    {
        var list = new List<string>();
        var r = new JsonReader(json);
        while (r.Read())
            list.Add(Describe(r.TokenType, r.GetStringRaw()));
        return list;
    }

    private static string Describe(TokenType tokenType, ReadOnlySpan<byte> value)
    {
        // Only tokens that publish a value span carry text; container tokens
        // leave the previous token's span in place.
        var hasText =
            tokenType
            is TokenType.String
                or TokenType.PropertyName
                or TokenType.Int32
                or TokenType.Int64
                or TokenType.UInt64
                or TokenType.Float64;
        return tokenType + ":" + (hasText ? Encoding.UTF8.GetString(value) : "");
    }

    // ── the four length classes: empty, single, escaped, long ──

    [Test]
    public async Task EmptyString_ReadsAsEmptyValue()
    {
        var value = ReadStringValue("\"\""u8.ToArray());
        await Assert.That(value).IsEqualTo("");
    }

    [Test]
    public async Task SingleChar_ReadsAsOneByte()
    {
        var value = ReadStringValue("\"a\""u8.ToArray());
        await Assert.That(value).IsEqualTo("a");
    }

    [Test]
    public async Task EmptyPropertyName_IsAPropertyNameToken()
    {
        var tokens = TokenTranscript("""{"":1}"""u8.ToArray());
        await Assert
            .That(tokens)
            .IsEquivalentTo(
                new List<string> { "ObjectStart:", "PropertyName:", "Int32:1", "ObjectEnd:" }
            );
    }

    [Test]
    public async Task ShortString_FollowedByColon_IsAPropertyName()
    {
        var tokens = TokenTranscript("""{"a":1}"""u8.ToArray());
        await Assert.That(tokens[1]).IsEqualTo("PropertyName:a");
    }

    [Test]
    public async Task PropertyName_WithWhitespaceBeforeColon_StaysAPropertyName()
    {
        var tokens = TokenTranscript("""{"a"   : 1}"""u8.ToArray());
        await Assert.That(tokens[1]).IsEqualTo("PropertyName:a");
        await Assert.That(tokens[2]).IsEqualTo("Int32:1");
    }

    [Test]
    public async Task StringValue_AtEndOfDocument_HasNoColonProbe()
    {
        var tokens = TokenTranscript("\"tail\""u8.ToArray());
        await Assert.That(tokens).IsEquivalentTo(new List<string> { "String:tail" });
    }

    // ── escapes: the token must decode exactly as before ──

    [Test]
    [Arguments("\"a\\\"b\"", "a\"b")]
    [Arguments("\"a\\\\b\"", "a\\b")]
    [Arguments("\"a\\/b\"", "a/b")]
    [Arguments("\"a\\bb\"", "a\bb")]
    [Arguments("\"a\\fb\"", "a\fb")]
    [Arguments("\"a\\nb\"", "a\nb")]
    [Arguments("\"a\\rb\"", "a\rb")]
    [Arguments("\"a\\tb\"", "a\tb")]
    [Arguments("\"\\u0041BC\"", "ABC")]
    [Arguments("\"caf\\u00e9\"", "café")]
    [Arguments("\"caf\\u00e9\\nlatte\"", "café\nlatte")]
    [Arguments("\"\\uD83D\\uDE00\"", "😀")]
    [Arguments("\"a\\u0000b\"", "a\0b")]
    public async Task EscapeSequence_DecodesLikeBefore(string json, string expected)
    {
        var value = ReadStringValue(Encoding.UTF8.GetBytes(json));
        await Assert.That(value).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\"héllo\"")]
    [Arguments("\"日本語\"")]
    [Arguments("\"😀 🎉\"")]
    public async Task RawNonAscii_IsPassedThroughUnchanged(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var value = ReadStringValue(bytes);
        await Assert.That(value).IsEqualTo(json[1..^1]);
    }

    [Test]
    public async Task RawControlByte_IsStillAcceptedVerbatim()
    {
        // The reader never rejected a bare control byte inside a string (the fast
        // path deliberately does not add that validation): the byte is the value.
        var value = ReadStringBytes([(byte)'"', 0x01, (byte)'"']);
        await Assert.That(value).IsEquivalentTo(new byte[] { 0x01 });
    }

    [Test]
    public async Task InvalidUtf8_IsPassedThroughWithoutValidation()
    {
        // No UTF-8 validation on this path, before or after: the raw bytes come back.
        var value = ReadStringBytes([(byte)'"', 0xFF, 0xC3, (byte)'"']);
        await Assert.That(value).IsEquivalentTo(new byte[] { 0xFF, 0xC3 });
    }

    [Test]
    public async Task EscapeAtTheVeryEndOfTheToken_IsDecoded()
    {
        var value = ReadStringValue("\"abc\\n\""u8.ToArray());
        await Assert.That(value).IsEqualTo("abc\n");
    }

    [Test]
    public async Task EscapedBackslashRightBeforeTheClosingQuote_TerminatesCorrectly()
    {
        // "a\\" — the escaped backslash must not be mistaken for the escape prefix
        // of the closing quote.
        var value = ReadStringValue("\"a\\\\\""u8.ToArray());
        await Assert.That(value).IsEqualTo("a\\");
    }

    // ── illegal input: same exception type and message ──

    [Test]
    [Arguments("\"\\q\"", "Invalid escape character 'q' at offset 1")]
    [Arguments("\"ab\\x\"", "Invalid escape character 'x' at offset 3")]
    [Arguments("\"\\u00ZZ\"", "Invalid unicode escape character 'Z' at offset 8")]
    [Arguments("\"\\uD800\"", "Lone high surrogate U+D800 at offset 8")]
    [Arguments("\"\\uDC00\"", "Lone low surrogate U+DC00 at offset 8")]
    [Arguments("\"\\uD800\\u0041\"", "Invalid low surrogate U+0041 at offset 14")]
    [Arguments("\"abc", "Unterminated string at offset 4")]
    [Arguments("\"abc\\", "Unterminated string at offset 5")]
    [Arguments("\"abc\\\"", "Unterminated string at offset 6")]
    [Arguments("\"\\u12\"", "Incomplete unicode escape at offset 6")]
    public async Task IllegalInput_ThrowsTheSameFormatException(string json, string expectedMessage)
    {
        var message = ReadStringError(Encoding.UTF8.GetBytes(json));
        await Assert.That(message).IsEqualTo(expectedMessage);
    }

    // ── long strings ──

    private static string Body(int length, char c) => new string(c, length);

    [Test]
    [Arguments(256)]
    [Arguments(1024)]
    [Arguments(65536)]
    public async Task LongPlainString_ReadsEveryByte(int length)
    {
        var body = Body(length, 'x');
        var value = ReadStringValue(Encoding.UTF8.GetBytes(Json(body)));
        await Assert.That(value.Length).IsEqualTo(length);
        await Assert.That(value).IsEqualTo(body);
    }

    [Test]
    public async Task LongString_WithEscapeInTheMiddle_IsUnescaped()
    {
        var body = Body(1000, 'x') + "\n" + Body(1000, 'y');
        var json = Json(body.Replace("\n", "\\n"));
        var value = ReadStringValue(Encoding.UTF8.GetBytes(json));
        await Assert.That(value).IsEqualTo(body);
    }

    [Test]
    public async Task LongString_WithUnicodeEscapePastTheFirstVector_IsUnescaped()
    {
        var body = Body(1000, 'x') + "é" + Body(1000, 'y');
        var json = Json(body.Replace("é", "\\u00e9"));
        var value = ReadStringValue(Encoding.UTF8.GetBytes(json));
        await Assert.That(value).IsEqualTo(body);
    }

    [Test]
    public async Task LongString_WithEscapeAsTheLastByte_IsUnescaped()
    {
        var body = Body(2000, 'x') + "\t";
        var value = ReadStringValue(Encoding.UTF8.GetBytes(Json(body.Replace("\t", "\\t"))));
        await Assert.That(value).IsEqualTo(body);
    }

    [Test]
    public async Task LongPropertyName_IsRecognisedAsAPropertyName()
    {
        var name = Body(4096, 'p');
        var tokens = TokenTranscript(Encoding.UTF8.GetBytes("{\"" + name + "\":7}"));
        await Assert.That(tokens[1]).IsEqualTo("PropertyName:" + name);
        await Assert.That(tokens[2]).IsEqualTo("Int32:7");
    }

    [Test]
    public async Task LongString_FollowedByAQuoteInside_MustNotStopEarly()
    {
        // An escaped quote past the 1 KB mark: the scan must keep going to the
        // real closing quote, not stop at the escaped one.
        var body = Body(1500, 'x') + "\"" + Body(10, 'y');
        var value = ReadStringValue(Encoding.UTF8.GetBytes(Json(body.Replace("\"", "\\\""))));
        await Assert.That(value).IsEqualTo(body);
    }

    // ── the same documents through the chunked (sequence) reader ──

    private static IEnumerable<byte[]> ChunkDocuments()
    {
        yield return Encoding.UTF8.GetBytes("""{"a":"b","c":"d"}""");
        yield return Encoding.UTF8.GetBytes("""{"na\"me":"va\\lue"}""");
        yield return Encoding.UTF8.GetBytes("""{"u":"caf\u00e9\nlatte","e":"\uD83D\uDE00"}""");
        yield return Encoding.UTF8.GetBytes(Json(Body(300, 'x')));
        yield return Encoding.UTF8.GetBytes(Json(Body(300, 'x') + "\n" + Body(300, 'y')));
        yield return Encoding.UTF8.GetBytes(
            "[" + string.Join(",", Enumerable.Repeat("\"" + Body(400, 'z') + "\"", 3)) + "]"
        );
    }

    [Test]
    [Timeout(120_000)]
    public async Task SequenceReader_MatchesSpanReader_ForEveryChunkSize(CancellationToken ct)
    {
        foreach (var doc in ChunkDocuments())
        {
            var expected = TokenTranscript(doc);
            foreach (var chunk in new[] { 1, 2, 3, 5, 7, 13, 64, 256, 1024, doc.Length })
            {
                if (chunk > doc.Length)
                    continue;
                var actual = TokenTranscript(Chunk(doc, chunk));
                await Assert.That(string.Join("|", actual)).IsEqualTo(string.Join("|", expected));
            }
        }
    }

    [Test]
    public async Task SequenceReader_SingleSegmentSequence_MatchesSpanReader()
    {
        var doc = Encoding.UTF8.GetBytes("""{"a":"b"}""");
        var tokens = TokenTranscript(new ReadOnlySequence<byte>(doc));
        await Assert.That(tokens[1]).IsEqualTo("PropertyName:a");
    }

    [Test]
    [Timeout(60_000)]
    public async Task SequenceReader_LongStringSplitAcrossSegments_IsIdentical(CancellationToken ct)
    {
        var body = Body(5000, 'q');
        var doc = Encoding.UTF8.GetBytes(Json(body));
        var expected = Body(5000, 'q');
        foreach (var chunk in new[] { 1, 3, 31, 4096, doc.Length })
        {
            var tokens = TokenTranscript(Chunk(doc, chunk));
            await Assert.That(tokens.Count).IsEqualTo(1);
            await Assert.That(tokens[0]).IsEqualTo("String:" + expected);
        }
    }

    [Test]
    [Timeout(60_000)]
    public async Task SequenceReader_EscapeSplitAcrossSegments_IsIdentical(CancellationToken ct)
    {
        // The chunk boundary falls inside the escape sequence and inside the
        // surrogate pair; the decoded value must be unchanged.
        var doc = Encoding.UTF8.GetBytes("\"ab\\uD83D\\uDE00cd\"");
        foreach (var chunk in new[] { 1, 2, 3, 4, 5, 7, 9, 11, 13, doc.Length })
        {
            var tokens = TokenTranscript(Chunk(doc, chunk));
            await Assert.That(tokens[0]).IsEqualTo("String:ab😀cd");
        }
    }

    [Test]
    public async Task SequenceReader_IllegalEscape_ThrowsAcrossASegmentBoundary()
    {
        var doc = Encoding.UTF8.GetBytes("\"ab\\qcd\"");
        var threw = false;
        try
        {
            TokenTranscript(Chunk(doc, 4));
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task SequenceReader_UnterminatedString_KeepsThePreExistingBehaviour()
    {
        // Sequence mode never threw for an unterminated string (it publishes the
        // remaining bytes); this task must not change that either way.
        var doc = Encoding.UTF8.GetBytes("\"abc");
        var tokens = TokenTranscript(Chunk(doc, 2));
        await Assert.That(tokens.Count).IsEqualTo(1);
        await Assert.That(tokens[0]).IsEqualTo("String:abc");
    }
}
