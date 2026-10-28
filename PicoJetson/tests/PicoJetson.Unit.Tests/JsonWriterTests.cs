namespace PicoJetson.Tests;

public class JsonWriterTests
{
    // By ref, and reading through writer.Buffer: the writer now caches a span, so the
    // bytes of an unfinished document are only on the wire after the hand-off. Passing a
    // JsonWriter *by value* would commit the copy's cursor and leave the caller's writer
    // pointing into memory it already committed -- the copy below would then corrupt the
    // output of anything written afterwards. Hence `ref`.
    private static string GetWrittenString(ref JsonWriter writer, ArrayBufferWriter<byte> buf)
    {
        _ = writer.Buffer; // documented hand-off point: commits the pending span
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    [Test]
    public async Task WriteNull_ProducesNull()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNull();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("null");
    }

    [Test]
    public async Task WriteBoolean_True()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteBoolean(true);
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("true");
    }

    [Test]
    public async Task WriteBoolean_False()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteBoolean(false);
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("false");
    }

    [Test]
    public async Task WriteNumber_Int32()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(42);
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("42");
    }

    [Test]
    public async Task WriteNumber_Int64()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(1234567890123L);
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("1234567890123");
    }

    [Test]
    public async Task WriteNumber_Double()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(3.14);
        await Assert.That(GetWrittenString(ref w, b)).Contains("3.14");
    }

    [Test]
    public async Task WriteString_ProducesQuotedString()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteString("hello"u8);
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("\"hello\"");
    }

    [Test]
    public async Task WriteObject_Empty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("{}");
    }

    [Test]
    public async Task WriteObject_WithProperty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WritePropertyName("name"u8);
        w.WriteString("alice"u8);
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("{\"name\":\"alice\"}");
    }

    [Test]
    public async Task WriteArray_Empty()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartArray();
        w.WriteEndArray();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("[]");
    }

    [Test]
    public async Task WriteArray_WithInts()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteStartArray();
        w.WriteNumber(1);
        w.WriteNumber(2);
        w.WriteNumber(3);
        w.WriteEndArray();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("[1,2,3]");
    }

    [Test]
    public async Task WriteNested_ObjectInObject()
    {
        var b = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(b);
        w.WriteStartObject();
        w.WritePropertyName("inner"u8);
        w.WriteStartObject();
        w.WritePropertyName("x"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("{\"inner\":{\"x\":1}}");
    }

    [Test]
    public async Task BytesWritten_Tracks()
    {
        var b = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(b);
        w.WriteNumber(42);
        await Assert.That(w.BytesWritten).IsEqualTo(2);
    }

    [Test]
    public async Task Indented_ProducesMultilineOutput()
    {
        var b = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(b, indented: true);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, b)).IsEqualTo("{\n  \"a\": 1\n}");
    }

    // === P0-1: String Escaping Tests ===

    [Test]
    public async Task WriteString_Escapes_Quote()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("he\"llo"u8); // contains embedded quote
        var json = GetWrittenString(ref w, buf);
        await Assert.That(json).IsEqualTo("\"he\\\"llo\"");
    }

    [Test]
    public async Task WriteString_Escapes_Backslash()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("a\\b"u8);
        var json = GetWrittenString(ref w, buf);
        await Assert.That(json).IsEqualTo("\"a\\\\b\"");
    }

    [Test]
    public async Task WriteString_Escapes_Newline()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString("line1\nline2"u8);
        var json = GetWrittenString(ref w, buf);
        await Assert.That(json).IsEqualTo("\"line1\\nline2\"");
    }

    // user bug: the char overload's long-string path (>256 UTF-8 bytes) called
    // the public byte overload, which ran BeforeWriteValue a second time and
    // emitted a spurious ',' — "\"content\":,\"D:\\...\"" broke strict parsers
    // with "expected value".
    [Test]
    public async Task WriteString_CharOverload_LongString_NoSpuriousComma()
    {
        var buf = new ArrayBufferWriter<byte>(512);
        var w = new JsonWriter(buf);
        var value = new string('a', 200) + "\"\\\n\u0000" + new string('b', 200); // >256 UTF-8 bytes, needs escaping
        w.WriteString(value);
        var json = GetWrittenString(ref w, buf);
        await Assert.That(json.StartsWith("\"")).IsTrue(); // starts with quote, not ','
        await Assert.That(json).DoesNotContain(",\"");
        // strict round-trip
        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement.GetString();
        await Assert.That(parsed).IsEqualTo(value);
    }

    // === P0-3: NaN/Infinity Tests ===

    [Test]
    public async Task WriteNumber_NaN_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        try
        {
            w.WriteNumber(double.NaN);
            await Assert.That(true).IsFalse(); // should not reach
        }
        catch (ArgumentException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task WriteNumber_Infinity_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        try
        {
            w.WriteNumber(double.PositiveInfinity);
            await Assert.That(true).IsFalse(); // should not reach
        }
        catch (ArgumentException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    // === P0 #1: maxDepth guard ===

    [Test]
    public async Task MaxDepth_Exceeded_ThrowsFormatException()
    {
        var buf = new ArrayBufferWriter<byte>(512);
        var w = new JsonWriter(buf, maxDepth: 63);

        // Fill depth to 63 (guard checks >= 63 before incrementing)
        for (int i = 0; i < 63; i++)
            w.WriteStartArray();

        // Depth is now 63 — the next Start should throw
        try
        {
            w.WriteStartArray();
            await Assert.That(true).IsFalse();
        }
        catch (FormatException)
        {
            await Assert.That(true).IsTrue();
        }
    }

    [Test]
    public async Task MaxDepth_WithinLimit_WritesCorrectly()
    {
        var buf = new ArrayBufferWriter<byte>(512);
        var w = new JsonWriter(buf, maxDepth: 63);

        // Nest 8 levels — well within limit
        for (int i = 0; i < 8; i++)
            w.WriteStartArray();
        w.WriteNumber(42);
        for (int i = 0; i < 8; i++)
            w.WriteEndArray();

        var result = GetWrittenString(ref w, buf);
        await Assert.That(result).IsEqualTo("[[[[[[[[42]]]]]]]]");
    }

    [Test]
    public async Task MaxDepth_Default_AllowsReasonableDepth()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);

        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WriteEndObject();

        var result = GetWrittenString(ref w, buf);
        await Assert.That(result).IsEqualTo("{\"a\":1}");
    }

    // === Code review: property name escaping (#1) ===

    [Test]
    public async Task WritePropertyName_EscapesQuote()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("he\"llo"u8);
        w.WriteNull();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{\"he\\\"llo\":null}");
    }

    [Test]
    public async Task WritePropertyName_EscapesBackslash()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a\\b"u8);
        w.WriteNull();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{\"a\\\\b\":null}");
    }

    [Test]
    public async Task WritePropertyName_EscapesNewline()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a\nb"u8);
        w.WriteNull();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{\"a\\nb\":null}");
    }

    // === Code review: control char escaping (#2) ===

    [Test]
    public async Task WriteString_EscapesControlChar_Null()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString(new byte[] { (byte)'a', 0x00, (byte)'b' });
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("\"a\\u0000b\"");
    }

    [Test]
    public async Task WriteString_EscapesControlChar_Escape()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteString(new byte[] { (byte)'x', 0x1B, (byte)'y' });
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("\"x\\u001By\"");
    }

    // === F1: closing a container while a property name is pending ===
    // A writer that emitted a property name but no value used to close the
    // container anyway, producing the illegal JSON {"a":} instead of failing
    // at the write site.
    //
    // JsonWriter is a ref struct, so every writer use has to finish before the
    // first await (CS4007): snapshots are taken into locals, assertions last.

    [Test]
    public async Task WriteEndObject_AfterPropertyNameWithoutValue_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);

        FormatException? caught = null;
        try
        {
            w.WriteEndObject();
        }
        catch (FormatException ex)
        {
            caught = ex;
        }

        // The failed close wrote no '}'.
        var afterFailedClose = GetWrittenString(ref w, buf);
        // _depth is untouched, so writing the value now still yields a
        // well-formed object instead of a half-closed one.
        w.WriteNumber(1);
        w.WriteEndObject();
        var afterValue = GetWrittenString(ref w, buf);

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("property name");
        await Assert.That(caught!.Message).Contains("no value");
        await Assert.That(afterFailedClose).IsEqualTo("{\"a\":");
        await Assert.That(afterValue).IsEqualTo("{\"a\":1}");
    }

    [Test]
    public async Task WriteEndArray_AfterPropertyNameWithoutValue_Throws()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartArray();
        w.WritePropertyName("a"u8);

        FormatException? caught = null;
        try
        {
            w.WriteEndArray();
        }
        catch (FormatException ex)
        {
            caught = ex;
        }

        var afterFailedClose = GetWrittenString(ref w, buf);
        w.WriteNumber(1); // _depth unchanged -> still inside the array
        w.WriteEndArray();
        var afterValue = GetWrittenString(ref w, buf);

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught!.Message).Contains("property name");
        await Assert.That(caught!.Message).Contains("no value");
        await Assert.That(afterFailedClose).IsEqualTo("[\"a\":");
        await Assert.That(afterValue).IsEqualTo("[\"a\":1]");
    }

    [Test]
    public async Task WriteEndObject_WithoutPropertyName_WritesCorrectly()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{}");
    }

    [Test]
    public async Task WriteEndObject_AfterPropertyNameWithValue_WritesCorrectly()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{\"a\":1}");
    }

    [Test]
    public async Task WriteEndObject_AfterFailedClose_DoesNotPolluteNextObject()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf);

        w.WriteStartArray();
        w.WriteStartObject();
        w.WritePropertyName("a"u8);

        var threw = false;
        try
        {
            w.WriteEndObject();
        }
        catch (FormatException)
        {
            threw = true;
        }

        // Finish the damaged object, then write a second one on the same writer
        // instance: depth/comma state must be intact (no stray '}' or ',').
        w.WriteNumber(1);
        w.WriteEndObject();
        w.WriteStartObject();
        w.WritePropertyName("b"u8);
        w.WriteNumber(2);
        w.WriteEndObject();
        w.WriteEndArray();
        var json = GetWrittenString(ref w, buf);

        await Assert.That(threw).IsTrue();
        await Assert.That(json).IsEqualTo("[{\"a\":1},{\"b\":2}]");
    }

    [Test]
    public async Task WriteEndObject_AfterFailedClose_Indented_WritesNoBraceOrIndent()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf, indented: true);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);

        var threw = false;
        try
        {
            w.WriteEndObject();
        }
        catch (FormatException)
        {
            threw = true;
        }

        var afterFailedClose = GetWrittenString(ref w, buf);
        w.WriteNumber(1);
        w.WriteEndObject();
        var afterValue = GetWrittenString(ref w, buf);

        await Assert.That(threw).IsTrue();
        await Assert.That(afterFailedClose).IsEqualTo("{\n  \"a\": ");
        await Assert.That(afterValue).IsEqualTo("{\n  \"a\": 1\n}");
    }

    // === F1 fix round 1: values written straight into Buffer ===
    // IJsonConverter<T>.Write only receives the bare IBufferWriter<byte>, and
    // JsonSerializer.SerializeCustom<T> is called with jw.Buffer as well, so those
    // values never pass through BeforeWriteValue. NotifyRawValueWritten() is the
    // explicit "the value is on the wire" signal the generator emits right after
    // every jw.Buffer write; without it the pending-property-name flag survives and
    // the next close (or the next value) is rejected/corrupted on valid JSON.

    [Test]
    public async Task NotifyRawValueWritten_AfterBufferValue_AllowsEndObject()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.Buffer.Write("\"2024-06-15\""u8); // converter-style direct buffer write
        w.NotifyRawValueWritten();
        w.WriteEndObject();
        await Assert.That(GetWrittenString(ref w, buf)).IsEqualTo("{\"a\":\"2024-06-15\"}");
    }

    [Test]
    public async Task NotifyRawValueWritten_AfterBufferValue_NextTopLevelObjectGetsComma()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("A"u8);
        w.Buffer.Write("\"2024-06-15\""u8);
        w.NotifyRawValueWritten();
        w.WriteEndObject();

        // Second top-level value on the SAME writer instance: a stale
        // _afterPropertyName flag makes BeforeWriteValue consume the comma slot,
        // so the two documents would run together without the ',' below.
        w.WriteStartObject();
        w.WritePropertyName("B"u8);
        w.WriteNumber(1);
        w.WriteEndObject();
        await Assert
            .That(GetWrittenString(ref w, buf))
            .IsEqualTo("{\"A\":\"2024-06-15\"},{\"B\":1}");
    }

    [Test]
    public async Task NotifyRawValueWritten_WithoutPendingPropertyName_IsIdempotentNoOp()
    {
        var buf = new ArrayBufferWriter<byte>(128);
        var w = new JsonWriter(buf);
        w.WriteStartArray();
        w.Buffer.Write("1"u8); // raw array element: no property name pending
        var bytesBeforeNotify = w.BytesWritten;
        w.NotifyRawValueWritten();
        w.NotifyRawValueWritten();
        var bytesAfterNotify = w.BytesWritten;
        w.WriteEndArray();
        var json = GetWrittenString(ref w, buf);

        await Assert.That(bytesAfterNotify).IsEqualTo(bytesBeforeNotify);
        await Assert.That(json).IsEqualTo("[1]");
    }
}
