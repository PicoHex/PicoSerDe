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

    // ---------------------------------------------------------------- maxDepth

    /// <summary>
    /// <c>maxDepth</c> is an <b>inclusive</b> ceiling: a writer configured with <c>maxDepth: 2</c>
    /// accepts a document that nests two containers, and refuses the container that would open the
    /// third at the write site (<see cref="FormatException"/>, "Maximum depth of 2 exceeded").
    /// <para>
    /// <b>Why this needs its own test.</b> Ablation on <c>0ae36ca</c>: widening the writer's
    /// <c>_depth &gt;= _maxDepth</c> to <c>_depth &gt; _maxDepth</c> — the classic off-by-one, one
    /// container too permissive — left the whole suite green. The tests that existed asked only the
    /// <b>positive</b> half ("exactly <c>maxDepth</c> levels can be written"), and a limit that lets
    /// one more level through answers that half correctly. A ceiling is only pinned when BOTH halves
    /// are: what fits, and the first thing that does not.
    /// </para>
    /// <para>
    /// The positive half stays in the suite on purpose, and next to the negative one: a limit
    /// tightened to <c>maxDepth - 1</c> (or a guard that rejects the level that merely reaches the
    /// ceiling) keeps the negative half green and turns this half red.
    /// </para>
    /// </summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MaxDepth_AcceptsExactlyThatManyLevels(int maxDepth)
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                for (var i = 0; i < maxDepth; i++)
                    w.WriteStartArray();
                w.WriteNumber(1);
                for (var i = 0; i < maxDepth; i++)
                    w.WriteEndArray();
            },
            maxDepth: maxDepth
        );

        var expected = new string('[', maxDepth) + "1" + new string(']', maxDepth);
        await Assert
            .That(json)
            .IsEqualTo(expected)
            .Because($"maxDepth: {maxDepth} is the deepest document this writer may write");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
    }

    /// <summary>
    /// The half that was missing: the level that would cross the ceiling throws, through
    /// <b>both</b> open members (<c>WriteStartArray</c> and <c>WriteStartObject</c> each carry their
    /// own depth check — a mutation of one is invisible to a test that only crosses with the other).
    /// </summary>
    [Test]
    [Arguments(1, "arrays")]
    [Arguments(2, "arrays")]
    [Arguments(1, "objects")]
    [Arguments(2, "objects")]
    public async Task MaxDepth_RejectsTheLevelThatCrossesIt(int maxDepth, string kind)
    {
        var ex = ThrowsDepth(
            kind == "arrays"
                ? (ref JsonWriter w) =>
                {
                    for (var i = 0; i <= maxDepth; i++)
                        w.WriteStartArray();
                }
                : (ref JsonWriter w) =>
                {
                    // Objects need a name for each level but the first: `{"n":{"n":{`.
                    for (var i = 0; i <= maxDepth; i++)
                    {
                        w.WriteStartObject();
                        if (i < maxDepth)
                            w.WritePropertyName("n"u8);
                    }
                },
            maxDepth
        );

        var message =
            "Maximum depth of "
            + maxDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " exceeded";
        await Assert
            .That(ex.Message)
            .Contains(message)
            .Because(
                $"opening {maxDepth + 1} nested {kind} with maxDepth: {maxDepth} must fail at the "
                    + "write site, and the message must name the configured limit"
            );
    }

    /// <summary>
    /// <see cref="Assert.Throws{T}"/> over a writer built with an explicit depth limit: the writer
    /// cannot be created outside the lambda (a <see cref="ref struct"/> cannot be captured), so the
    /// fixture does what the success-path <see cref="Write"/> does, inside it.
    /// </summary>
    private static FormatException ThrowsDepth(WriterAction write, int maxDepth)
    {
        var buf = new ArrayBufferWriter<byte>(8);
        return Assert.Throws<FormatException>(() =>
        {
            var w = new JsonWriter(buf, maxDepth: maxDepth);
            write(ref w);
        });
    }
}
