namespace PicoJetson.Tests;

/// <summary>
/// Misuse of <see cref="JsonWriter"/> must fail at the write site instead of producing illegal
/// JSON for the consumer to discover later.
/// </summary>
/// <remarks>
/// <para>
/// Four calls used to be accepted silently and then emit a document no reader accepts — the
/// same four <c>System.Text.Json.Utf8JsonWriter</c> rejects with
/// <see cref="InvalidOperationException"/>:
/// </para>
/// <list type="number">
///   <item>W1 — a value inside an object with no property name pending: <c>{"x"}</c>;</item>
///   <item>W2 — two property names in a row, no value between them: <c>{"a":,"b":1}</c>;</item>
///   <item>W3 — an <c>End*</c> with no matching open (depth would go to −1): the bytes then
///   either never commit (empty output) or the extra brace is swallowed, so the document reads
///   as valid while the caller's bookkeeping is already broken;</item>
///   <item>W4 — an object closed with <c>']'</c>: <c>{]</c>.</item>
/// </list>
/// <para>
/// W1 and W4 need to know <em>which container is open</em>, which the writer did not track: the
/// guard is the one-bit-per-depth <c>_kinds</c> mask, and the maximum-depth case is part of the
/// contract (63 levels, one bit each), so a writer at <c>maxDepth: 63</c> is covered here too —
/// that is where a mask one bit too narrow would report a phantom type error.
/// </para>
/// </remarks>
public class JsonWriterMisuseGuardTests
{
    private delegate void WriterAction(ref JsonWriter w);

    private static string Write(WriterAction write, int maxDepth = 63)
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf, maxDepth: maxDepth);
        write(ref w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    private static InvalidOperationException Throws(WriterAction write)
    {
        var buf = new ArrayBufferWriter<byte>(8);
        return Assert.Throws<InvalidOperationException>(() =>
        {
            var w = new JsonWriter(buf);
            write(ref w);
        });
    }

    // ---------------------------------------------------------------- W1

    [Test]
    public async Task W1_Value_written_inside_an_object_without_a_property_name_throws()
    {
        // Before the guard: no exception, and the buffer held `{"x"}` — a document
        // PicoDocument.IsValid rejects, discovered by the consumer, not by the caller.
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteString("x");
                w.WriteEndObject();
            }
        );
        await Assert.That(ex.Message).Contains("without a property name");
    }

    [Test]
    public async Task W1_Number_written_inside_an_object_without_a_property_name_throws()
    {
        // The number path stages its own separator instead of going through
        // BeforeWriteValue, so it needs the same question asked (same misuse: `{1}`).
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumber(1);
            }
        );
        await Assert.That(ex.Message).Contains("without a property name");
    }

    [Test]
    public async Task W1_Rejection_happens_before_anything_is_written()
    {
        // A guard that threw *after* writing would leave the caller a half-built document —
        // the failure mode these guards exist to remove.
        var buf = new ArrayBufferWriter<byte>(8);
        var threw = false;
        try
        {
            var w = new JsonWriter(buf);
            w.WriteStartObject();
            w.WriteString("x");
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(buf.WrittenSpan)).IsEqualTo("{");
    }

    // ---------------------------------------------------------------- W2

    [Test]
    public async Task W2_Two_property_names_in_a_row_throw()
    {
        // Before the guard: `{"a":,"b":1}` — the second name even collected a separator, so
        // the output was illegal rather than merely odd.
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WritePropertyName("b"u8);
                w.WriteNumber(1);
                w.WriteEndObject();
            }
        );
        await Assert.That(ex.Message).Contains("property name");
        await Assert.That(ex.Message).Contains("missing");
    }

    // ---------------------------------------------------------------- W3

    [Test]
    public async Task W3_EndObject_without_a_matching_open_throws()
    {
        // Before the guard: depth went to −1 and every later `}` failed to commit — the extra
        // close was silent, and after a complete `{"a":1}` it was swallowed entirely.
        var ex = Throws((ref JsonWriter w) => w.WriteEndObject());
        await Assert.That(ex.Message).Contains("'}' is invalid without a matching open");
    }

    [Test]
    public async Task W3_Extra_EndObject_after_a_complete_document_throws()
    {
        // The shape that used to be worst: `{"a":1}` plus one extra close read as valid JSON,
        // so the caller's mistake never surfaced at all.
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(1);
                w.WriteEndObject();
                w.WriteEndObject();
            }
        );
        await Assert.That(ex.Message).Contains("'}' is invalid without a matching open");
    }

    [Test]
    public async Task W3_EndArray_without_a_matching_open_throws()
    {
        var ex = Throws((ref JsonWriter w) => w.WriteEndArray());
        await Assert.That(ex.Message).Contains("']' is invalid without a matching open");
    }

    // ---------------------------------------------------------------- W4

    [Test]
    public async Task W4_EndArray_closing_an_object_throws()
    {
        // Before the guard: `{]` — the writer never knew the open container was an object.
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteEndArray();
            }
        );
        await Assert.That(ex.Message).Contains("']' is invalid without a matching open");
    }

    [Test]
    public async Task W4_EndObject_closing_an_array_throws()
    {
        // The symmetric half of W4, and the one that was missing: `WriteStartArray()` then
        // `WriteEndObject()` produced the document `[}` with no exception at all — illegal
        // JSON handed to the consumer, discovered by PicoDocument.IsValid, not by the caller.
        // WriteEndArray has asked "is the open container really an array?" from the start;
        // WriteEndObject never asked the same question about an object.
        var ex = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteEndObject();
            }
        );
        await Assert.That(ex.Message).Contains("'}' is invalid without a matching open");
    }

    [Test]
    public async Task W4_EndObject_closing_an_array_writes_nothing()
    {
        // Same contract as every other guard: the question is asked before any mutation, so a
        // rejected close leaves the buffer with exactly the bytes that were legitimately
        // written — the `[` — and the depth untouched (the array is still open for its own
        // `]`).
        var buf = new ArrayBufferWriter<byte>(8);
        var threw = false;
        try
        {
            var w = new JsonWriter(buf);
            w.WriteStartArray();
            w.WriteEndObject();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(buf.WrittenSpan)).IsEqualTo("[");
    }

    // -------------------------------------------------- reverse protection

    [Test]
    public async Task Legal_uses_of_every_container_kind_still_write_the_same_bytes()
    {
        // The guards must not fire on correct code: a value inside an array needs no name, a
        // value after a property name is the normal case, and a nested object opens from a
        // pending name. All three mix in one document, byte for byte.
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteStartArray();
                w.WriteString("x");
                w.WriteNumber(1);
                w.WriteNull();
                w.WriteStartObject();
                w.WritePropertyName("b"u8);
                w.WriteBoolean(true);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WritePropertyName("c"u8);
                w.WriteStartObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }
        );
        await Assert.That(json).IsEqualTo("""{"a":["x",1,null,{"b":true}],"c":{}}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
    }

    [Test]
    public async Task Top_level_values_still_write_without_any_container()
    {
        // Depth 0 is not "inside an object", and the kind mask is never set there — a guard
        // that read the bit at depth 0 would reject every top-level scalar.
        await Assert.That(Write((ref JsonWriter w) => w.WriteString("x"))).IsEqualTo("\"x\"");
        await Assert.That(Write((ref JsonWriter w) => w.WriteNumber(42))).IsEqualTo("42");
        await Assert.That(Write((ref JsonWriter w) => w.WriteNull())).IsEqualTo("null");
        await Assert.That(Write((ref JsonWriter w) => w.WriteRawValue("{ }"u8))).IsEqualTo("{ }");
    }

    [Test]
    public async Task The_deepest_supported_nesting_still_writes_objects_and_arrays()
    {
        // maxDepth 63 puts the innermost container on bit 63 of the kind mask: the boundary a
        // mask that were one bit short (32, or 62) would answer wrongly — either reporting a
        // phantom object where an array is open, or losing the kind test altogether.
        var json = Write(
            (ref JsonWriter w) =>
            {
                for (var i = 0; i < 62; i++)
                    w.WriteStartArray();
                w.WriteStartObject();
                w.WritePropertyName("deep"u8);
                w.WriteNumber(1);
                w.WriteEndObject();
                for (var i = 0; i < 62; i++)
                    w.WriteEndArray();
            },
            maxDepth: 63
        );
        var expected = new string('[', 62) + """{"deep":1}""" + new string(']', 62);
        await Assert.That(json).IsEqualTo(expected);
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
    }
}
