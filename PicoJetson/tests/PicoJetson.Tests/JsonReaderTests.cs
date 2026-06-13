namespace PicoJetson.Tests;

public class JsonReaderTests
{
    [Test]
    public async Task ReadNull_ReturnsNullToken()
    {
        var r = new JsonReader("null"u8);
        var ok = r.Read();
        var tt = r.TokenType;
        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.Null);
    }

    [Test]
    public async Task ReadTrue_ReturnsBoolToken()
    {
        var r = new JsonReader("true"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadFalse_ReturnsBoolToken()
    {
        var r = new JsonReader("false"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadInteger_ReturnsInt32Token()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadLeadingZeroInteger_ThrowsFormatException()
    {
        var r = new JsonReader("01"u8);
        try
        {
            r.Read();
            throw new Exception("Expected FormatException");
        }
        catch (FormatException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task ReadLeadingZeroNegative_ThrowsFormatException()
    {
        var r = new JsonReader("-01"u8);
        try
        {
            r.Read();
            throw new Exception("Expected FormatException");
        }
        catch (FormatException) { }
        await Assert.That(true).IsTrue();
    }

    [Test]
    public async Task ReadNegativeInteger_ReturnsInt32()
    {
        var r = new JsonReader("-17"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadFloat_ReturnsFloat64Token()
    {
        var r = new JsonReader("3.14"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Float64);
    }

    [Test]
    public async Task ReadString_ReturnsStringToken()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task GetStringRaw_ReturnsDecodedBytes()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        var str = Encoding.UTF8.GetString(raw);
        await Assert.That(str).IsEqualTo("hello");
    }

    [Test]
    public async Task TryGetInt32_ParsesValue()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var ok = r.TryGetInt32(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetInt32_ReturnsFalse_OnString()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var ok = r.TryGetInt32(out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetFloat64_ParsesValue()
    {
        var r = new JsonReader("3.14"u8);
        r.Read();
        var ok = r.TryGetFloat64(out var v);
        var diff = Math.Abs(v - 3.14);
        await Assert.That(ok).IsTrue();
        await Assert.That(diff).IsLessThan(0.001);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var r = new JsonReader("true"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task ReadEmptyObject_ReturnsStartEnd()
    {
        var r = new JsonReader("{}"u8);
        var ok1 = r.Read();
        var tt1 = r.TokenType;
        var ok2 = r.Read();
        var tt2 = r.TokenType;
        var ok3 = r.Read();
        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(ok3).IsFalse();
    }

    [Test]
    public async Task ReadEmptyArray_ReturnsStartEnd()
    {
        var r = new JsonReader("[]"u8);
        r.Read();
        var tt1 = r.TokenType;
        r.Read();
        var tt2 = r.TokenType;
        await Assert.That(tt1).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(tt2).IsEqualTo(TokenType.ArrayEnd);
    }

    [Test]
    public async Task ReadObjectWithProperty()
    {
        var r = new JsonReader("{\"name\":\"alice\"}"u8);
        r.Read();
        var tt1 = r.TokenType;
        r.Read();
        var tt2 = r.TokenType;
        r.Read();
        var tt3 = r.TokenType;
        r.Read();
        var tt4 = r.TokenType;
        var ok5 = r.Read();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(tt3).IsEqualTo(TokenType.String);
        await Assert.That(tt4).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(ok5).IsFalse();
    }

    [Test]
    public async Task Depth_TracksNesting()
    {
        var r = new JsonReader("{\"a\":[1,2]}"u8);
        r.Read();
        var d1 = r.Depth;
        r.Read();
        var d2 = r.Depth;
        r.Read();
        var d3 = r.Depth;
        r.Read();
        var d4 = r.Depth;
        r.Read();
        var d5 = r.Depth;
        r.Read();
        var d6 = r.Depth;
        r.Read();
        var d7 = r.Depth;
        await Assert.That(d1).IsEqualTo(1);
        await Assert.That(d2).IsEqualTo(1);
        await Assert.That(d3).IsEqualTo(2);
        await Assert.That(d4).IsEqualTo(2);
        await Assert.That(d5).IsEqualTo(2);
        await Assert.That(d6).IsEqualTo(1);
        await Assert.That(d7).IsEqualTo(0);
    }

    [Test]
    public async Task BytesConsumed_MatchesInput()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var bc = r.BytesConsumed;
        await Assert.That(bc).IsEqualTo(2);
    }

    [Test]
    public async Task Skip_Object_AdvancesPastIt()
    {
        var r = new JsonReader("{\"a\":1} \"next\""u8);
        r.Read();
        r.Skip();
        var ok = r.Read();
        var tt = r.TokenType;
        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task TrySkip_ReturnsFalse_OnMalformed()
    {
        var r = new JsonReader("{broken"u8);
        r.Read();
        var ok = r.TrySkip();
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ValueSpan_ContainsRawBytes()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var str = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(str).IsEqualTo("42");
    }

    // === P0-2: String Unescaping Tests ===

    [Test]
    public async Task GetStringRaw_Unescapes_Quote()
    {
        var r = new JsonReader("\"he\\\"llo\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("he\"llo");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Backslash()
    {
        var r = new JsonReader("\"a\\\\b\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("a\\b");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Newline()
    {
        var r = new JsonReader("\"line1\\nline2\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("line1\nline2");
    }

    // === Unicode escape tests ===

    [Test]
    public async Task GetStringRaw_Unescapes_Unicode_BMP()
    {
        // \u00e9 = é (U+00E9 → UTF-8: 0xC3 0xA9)
        var r = new JsonReader("\"caf\\u00e9\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("café");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Unicode_ASCII()
    {
        // \u0041 = 'A'
        var r = new JsonReader("\"\\u0041BC\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("ABC");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Unicode_SurrogatePair()
    {
        // \uD83D\uDE00 = 😀 (U+1F600 → UTF-8: 0xF0 0x9F 0x98 0x80)
        var r = new JsonReader("\"\\uD83D\\uDE00\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("😀");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_MixedEscapes()
    {
        // \u00e9 = é, \n = newline
        var r = new JsonReader("\"caf\\u00e9\\nlatte\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("café\nlatte");
    }

    [Test]
    public async Task InvalidUnicodeEscape_NonHex_Throws()
    {
        var r = new JsonReader("\"\\u00ZZ\""u8);
        try
        {
            r.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task InvalidUnicodeEscape_LoneSurrogate_Throws()
    {
        // \uD800 without matching low surrogate
        var r = new JsonReader("\"\\uD800\""u8);
        try
        {
            r.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    // === P1-7: Error Message Tests ===

    [Test]
    public async Task MalformedJson_ErrorHasOffsetInfo()
    {
        try
        {
            var r = new JsonReader("{\n  \"a\": broken\n}"u8);
            while (r.Read()) { }
            await Assert.That(true).IsFalse();
        }
        catch (FormatException ex)
        {
            await Assert.That(ex.Message).Contains("offset");
        }
    }

    // === P2: ReadOnlySequence support ===

    [Test]
    public async Task SequenceReader_ParsesSimpleJson()
    {
        // Wrap in non-async scope to avoid ref struct crossing await boundary
        TokenType tt1,
            tt2,
            tt3,
            tt4;
        bool ok1,
            ok2,
            ok3,
            ok4;
        {
            var json = "{\"a\":1}"u8.ToArray();
            var seq = new ReadOnlySequence<byte>(json);
            var r = new JsonReader(seq);
            ok1 = r.Read();
            tt1 = r.TokenType;
            ok2 = r.Read();
            tt2 = r.TokenType;
            ok3 = r.Read();
            tt3 = r.TokenType;
            ok4 = r.Read();
            tt4 = r.TokenType;
        }
        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(ok3).IsTrue();
        await Assert.That(tt3).IsEqualTo(TokenType.Int32);
        await Assert.That(ok4).IsTrue();
        await Assert.That(tt4).IsEqualTo(TokenType.ObjectEnd);
    }

    [Test]
    public async Task SequenceReader_MultiSegment()
    {
        string rawName,
            rawValue;
        {
            var part1 = "{\"nam"u8.ToArray();
            var part2 = "e\":\"alice\"}"u8.ToArray();
            var seg1 = new ReadOnlySequenceSegment<byte>(new ReadOnlyMemory<byte>(part1), 0);
            var seg2 = seg1.Append(new ReadOnlyMemory<byte>(part2));
            var seq = new ReadOnlySequence<byte>(seg1, 0, seg2, part2.Length);
            var r = new JsonReader(seq);
            r.Read();
            r.Read();
            rawName = Encoding.UTF8.GetString(r.GetStringRaw());
            r.Read();
            rawValue = Encoding.UTF8.GetString(r.GetStringRaw());
        }
        await Assert.That(rawName).IsEqualTo("name");
        await Assert.That(rawValue).IsEqualTo("alice");
    }

    private sealed class ReadOnlySequenceSegment<T> : System.Buffers.ReadOnlySequenceSegment<T>
    {
        public ReadOnlySequenceSegment(ReadOnlyMemory<T> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public ReadOnlySequenceSegment<T> Append(ReadOnlyMemory<T> memory)
        {
            var seg = new ReadOnlySequenceSegment<T>(memory, RunningIndex + Memory.Length);
            Next = seg;
            return seg;
        }
    }

    // === SIMD SkipWhitespace ===

    [Test]
    public async Task TryReadNextInt32_ParsesValuesSkippingCommas()
    {
        var r = new JsonReader("[1, 20, 300]"u8);
        r.Read(); // enter ArrayStart
        var ok1 = r.TryReadNextInt32(out var v1);
        var ok2 = r.TryReadNextInt32(out var v2);
        var ok3 = r.TryReadNextInt32(out var v3);
        await Assert.That(ok1).IsTrue();
        await Assert.That(v1).IsEqualTo(1);
        await Assert.That(ok2).IsTrue();
        await Assert.That(v2).IsEqualTo(20);
        await Assert.That(ok3).IsTrue();
        await Assert.That(v3).IsEqualTo(300);
    }

    [Test]
    public async Task TryReadNextInt32_CompactArray()
    {
        int v1,
            v2,
            v3;
        bool ok1,
            ok2,
            ok3;
        {
            var r = new JsonReader("[100,200,300]"u8);
            r.Read();
            ok1 = r.TryReadNextInt32(out v1);
            ok2 = r.TryReadNextInt32(out v2);
            ok3 = r.TryReadNextInt32(out v3);
        }
        await Assert.That(ok1).IsTrue();
        await Assert.That(v1).IsEqualTo(100);
        await Assert.That(v2).IsEqualTo(200);
        await Assert.That(v3).IsEqualTo(300);
    }

    // === MaxDepth defense ===

    [Test]
    public async Task Depth_WithinLimit_ParsesSuccessfully()
    {
        var json = new string('{', 10) + "\"a\":" + new string('}', 10);
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        while (r.Read()) { }
        await Assert.That(r.Depth).IsEqualTo(0);
    }

    [Test]
    public async Task Depth_ExceedsDefault_Throws()
    {
        var json = new string('[', 260) + new string(']', 260);
        var r = new JsonReader(Encoding.UTF8.GetBytes(json));
        try
        {
            while (r.Read()) { }
            await Assert.That(true).IsFalse();
        }
        catch (FormatException ex)
        {
            await Assert.That(ex.Message).Contains("depth");
        }
    }

    // === P0 #2: ArrayPool buffer leak — many escaped strings trigger >8 TrackBuffer calls ===

    [Test]
    public async Task Dispose_ReturnsAllTrackedBuffers()
    {
        // JSON with 12 escaped strings forces >8 TrackBuffer calls via UnescapeIfNeeded.
        // Each "\\n" → actual backslash-n escape → triggers buffer rental.
        var jsonStr =
            "{"
            + "\"a\":\"\\n\","
            + "\"b\":\"\\n\","
            + "\"c\":\"\\n\","
            + "\"d\":\"\\n\","
            + "\"e\":\"\\n\","
            + "\"f\":\"\\n\","
            + "\"g\":\"\\n\","
            + "\"h\":\"\\n\","
            + "\"i\":\"\\n\","
            + "\"j\":\"\\n\","
            + "\"k\":\"\\n\","
            + "\"l\":\"\\n\"}";

        var json = Encoding.UTF8.GetBytes(jsonStr);
        var reader = new JsonReader(json);

        // Read all tokens, collecting values
        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == TokenType.String)
                values.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
        }

        reader.Dispose();

        // Capture values before any await (ref struct can't cross await boundary)
        var capturedValues = values;
        var capturedPeak = reader.PeakTrackedBufferCount;
        var capturedLeaked = reader.LeakedBufferCount;
        var capturedCount = reader.TrackedBufferCount;

        // All 12 string values should be "\n" (newline)
        await Assert.That(capturedValues.Count).IsEqualTo(12);
        foreach (var v in capturedValues)
            await Assert.That(v).IsEqualTo("\n");

        // Prove >8 buffers were tracked (overflowed the 8-slot tracking array)
        await Assert.That(capturedPeak).IsGreaterThan(8);

        // Prove all tracked buffers were returned (LeakedBufferCount == 0)
        await Assert.That(capturedLeaked).IsEqualTo(0);

        // Prove _bufCount was reset
        await Assert.That(capturedCount).IsEqualTo(0);
    }

    // === Code review #3: strict structural validation ===

    [Test]
    public async Task UnmatchedObjectEnd_AtDepthZero_ThrowsFormatException()
    {
        var reader = new JsonReader("}"u8);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException ex)
        {
            await Assert.That(ex.Message).Contains("Unmatched");
        }
    }

    [Test]
    public async Task UnmatchedArrayEnd_AtDepthZero_ThrowsFormatException()
    {
        var reader = new JsonReader("]"u8);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException ex)
        {
            await Assert.That(ex.Message).Contains("Unmatched");
        }
    }

    [Test]
    public async Task TrailingComma_InObject_ThrowsFormatException()
    {
        // {"a":1,} — trailing comma before }
        var reader = new JsonReader("{\"a\":1,}"u8);
        try
        {
            while (reader.Read()) { }
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task TrailingComma_InArray_ThrowsFormatException()
    {
        // [1,2,] — trailing comma before ]
        var reader = new JsonReader("[1,2,]"u8);
        try
        {
            while (reader.Read()) { }
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task TrailingGarbage_AfterRootObject_ThrowsFormatException()
    {
        // {}garbage — extra data after valid JSON
        var reader = new JsonReader("{}g"u8);
        reader.Read(); // {
        reader.Read(); // }
        // Next read should fail because of trailing garbage
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    // === Code review #4: strict number validation ===

    [Test]
    public async Task LoneMinus_ThrowsFormatException()
    {
        var reader = new JsonReader("-"u8);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task NumberWithDotButNoFraction_ThrowsFormatException()
    {
        var reader = new JsonReader("1."u8);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task NumberWithExpButNoDigits_ThrowsFormatException()
    {
        var reader = new JsonReader("1e"u8);
        try
        {
            reader.Read();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    // ── Streaming / isFinalBlock tests ──

    [Test]
    public async Task Read_IsFinalBlock_EndOfData_NeedsMoreDataFalse()
    {
        bool result,
            needsMore;
        {
            var r = new JsonReader("{}"u8, isFinalBlock: true);
            r.Read();
            r.Read();
            result = r.Read();
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsFalse();
    }

    [Test]
    public async Task Read_NotFinalBlock_EndOfData_NeedsMoreDataTrue()
    {
        bool result,
            needsMore;
        {
            var r = new JsonReader("{}"u8, isFinalBlock: false);
            r.Read();
            r.Read();
            result = r.Read();
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task Read_NotFinalBlock_StringTruncated_NeedMoreData()
    {
        // Stream ends in the MIDDLE of a string: the closing " is truly missing.
        // HasCompletePropertyOrString must detect this and signal NeedMoreData.
        bool result,
            needsMore;
        {
            var seq = new ReadOnlySequence<byte>("{\"a\":\"hel"u8.ToArray());
            var r = new JsonReader(seq, isFinalBlock: false);
            r.Read();
            r.Read(); // { and "a" (complete, : is present)
            result = r.Read(); // try to read value -> should fail
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task Read_NotFinalBlock_NameTruncated_NeedMoreData()
    {
        // Stream ends after the closing " of a property name, no : or subsequent byte.
        // The property name alone is complete, but HasCompletePropertyOrString
        // requires a byte after the closing " to determine PropertyName vs String.
        bool result,
            needsMore;
        {
            var seq = new ReadOnlySequence<byte>("{\"name\""u8.ToArray());
            var r = new JsonReader(seq, isFinalBlock: false);
            r.Read(); // {
            result = r.Read(); // try to read "name" -> should fail because no : follows
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task ExportState_PreservesDepth()
    {
        int depth;
        {
            var r = new JsonReader("{\"a\":{\"b\":1}}"u8, isFinalBlock: false);
            r.Read(); // outer {
            r.Read(); // "a"
            r.Read(); // inner {
            var state = r.ExportState();
            depth = state.Depth;
        }
        // At this point depth should be 2 (outer + inner object)
        await Assert.That(depth).IsEqualTo(2);
    }

    [Test]
    public async Task ExportState_RoundTrip_RestoresDepth()
    {
        int finalDepth;
        {
            // Parse first half, save state, then resume from state
            var part1 = "{\"a\":1,\"b\":2"u8.ToArray();
            var seq1 = new ReadOnlySequence<byte>(part1);
            var r1 = new JsonReader(seq1, isFinalBlock: false);
            r1.Read();
            r1.Read();
            r1.Read();
            r1.Read(); // consume up to part boundary
            // After reading "b", the next Read() hits end of seq1
            var state = r1.ExportState();

            // Simulate the streaming extension method:
            // new buffer starts where the old one ended
            var part2 = "}"u8.ToArray();
            var seq2 = new ReadOnlySequence<byte>(part2);
            var r2 = new JsonReader(seq2, isFinalBlock: true, state);
            r2.Read(); // should read the remaining "}"
            finalDepth = r2.Depth;
        }
        await Assert.That(finalDepth).IsEqualTo(0);
    }
}
