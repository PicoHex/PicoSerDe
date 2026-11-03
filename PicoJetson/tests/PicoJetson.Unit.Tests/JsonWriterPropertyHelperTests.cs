using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// "Name and value in one call" — the entry point whose absence made every downstream helper
/// write <see cref="JsonWriter.WritePropertyName"/> + <c>WriteX</c> by hand, and therefore take
/// the writer by <c>ref</c> and get the pairing wrong by itself.
/// </summary>
/// <remarks>
/// <para>
/// The contract these overloads are held to is <b>byte identity</b>: for every legal use,
/// <c>WriteXProperty(name, value)</c> writes exactly the bytes
/// <c>WritePropertyName(name); WriteX(value)</c> writes — same separator, same escaping, same
/// indentation, same number formatting, same failure. That is what makes the one-call form a
/// safe drop-in rather than a second dialect of the same document.
/// </para>
/// <para>
/// One deliberate difference, and it is a narrowing, not a dialect: the helpers require an
/// <em>object</em> to write into. At the root or inside an array, <c>"a":</c> is not JSON at
/// all, and the two-call form emits it silently — the same disease as <c>[}</c>. So the helpers
/// throw there, before a byte is staged.
/// </para>
/// <para>
/// The overload list is chosen, not exhaustive: <c>string</c> (UTF-8 and UTF-16), <c>int</c>,
/// <c>long</c>, <c>double</c>, <c>bool</c>, <c>null</c> — the six kinds a member is made of in
/// this stack, each with a <c>WriteX</c> sibling to delegate to. There is no <c>float</c> twin
/// on purpose: a <c>float</c> argument would silently widen into the <c>double</c> overload and
/// print <c>0.10000000149011612</c> where <c>WriteNumber(float)</c> prints <c>0.1</c> — a quiet
/// digit change is exactly the class of failure these helpers exist to remove, so the caller
/// holding a <c>float</c> keeps the two-call form or casts knowingly.
/// </para>
/// </remarks>
public class JsonWriterPropertyHelperTests
{
    private delegate void WriterAction(ref JsonWriter w);

    /// <summary>Runs <paramref name="write"/> on a fresh writer and returns the bytes it produced.</summary>
    private static string Write(
        WriterAction write,
        bool indented = false,
        JsonOptions? options = null
    )
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf, indented: indented, options: options);
        write(ref w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    // ── the six kinds, one overload each ────────────────────────────────────

    [Test]
    public async Task Null_property_writes_the_null_literal()
    {
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNullProperty("a"u8);
                        w.WriteEndObject();
                    }
                )
            )
            .IsEqualTo("""{"a":null}""");
    }

    [Test]
    public async Task Boolean_property_writes_true_and_false()
    {
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteBooleanProperty("a"u8, true);
                        w.WriteBooleanProperty("b"u8, false);
                        w.WriteEndObject();
                    }
                )
            )
            .IsEqualTo("""{"a":true,"b":false}""");
    }

    [Test]
    public async Task Int_property_writes_the_extremes_without_a_change_of_type()
    {
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNumberProperty("min"u8, int.MinValue);
                        w.WriteNumberProperty("max"u8, int.MaxValue);
                        w.WriteEndObject();
                    }
                )
            )
            .IsEqualTo("""{"min":-2147483648,"max":2147483647}""");
    }

    [Test]
    public async Task Long_property_writes_values_beyond_the_int_range()
    {
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNumberProperty("v"u8, 9_223_372_036_854_775_807L);
                        w.WriteEndObject();
                    }
                )
            )
            .IsEqualTo("""{"v":9223372036854775807}""");
    }

    [Test]
    public async Task Double_property_writes_the_shortest_round_trip_text()
    {
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNumberProperty("v"u8, 1234.5);
                        w.WriteEndObject();
                    }
                )
            )
            .IsEqualTo("""{"v":1234.5}""");
    }

    [Test]
    public async Task String_property_writes_both_span_flavours_identically()
    {
        var utf8 = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a"u8, "héllo"u8);
                w.WriteEndObject();
            }
        );
        var utf16 = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a"u8, "héllo");
                w.WriteEndObject();
            }
        );
        await Assert.That(utf8).IsEqualTo("""{"a":"héllo"}""");
        await Assert.That(utf16).IsEqualTo(utf8);
    }

    // ── byte identity with the two-call form ────────────────────────────────

    [Test]
    public async Task Every_overload_writes_the_same_bytes_as_the_two_call_form()
    {
        foreach (var (name, byHelper, byHand) in Pairs())
            await Assert.That(Write(byHelper)).IsEqualTo(Write(byHand)).Because(name);
    }

    [Test]
    public async Task Every_overload_writes_the_same_bytes_as_the_two_call_form_when_indented()
    {
        // Indentation is where the two paths could diverge most easily: the colon carries a
        // trailing space there, and the indent is written by the name path.
        foreach (var (name, byHelper, byHand) in Pairs())
            await Assert
                .That(Write(byHelper, indented: true))
                .IsEqualTo(Write(byHand, indented: true))
                .Because(name);
    }

    private static IEnumerable<(string Name, WriterAction Helper, WriterAction Hand)> Pairs()
    {
        yield return (
            "null",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNullProperty("a"u8);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNull();
                w.WriteEndObject();
            }
        );
        yield return (
            "bool",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteBooleanProperty("a"u8, true);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteBoolean(true);
                w.WriteEndObject();
            }
        );
        yield return (
            "int",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumberProperty("a"u8, -7);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(-7);
                w.WriteEndObject();
            }
        );
        yield return (
            "long",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumberProperty("a"u8, -7L);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(-7L);
                w.WriteEndObject();
            }
        );
        yield return (
            "double",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumberProperty("a"u8, 0.1);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(0.1);
                w.WriteEndObject();
            }
        );
        yield return (
            "string (utf8)",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a"u8, "v"u8);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteString("v"u8);
                w.WriteEndObject();
            }
        );
        yield return (
            "string (utf16)",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a"u8, "v");
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteString("v");
                w.WriteEndObject();
            }
        );
        yield return (
            "escapable name and value",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a\"b"u8, "l1\nl2\\");
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a\"b"u8);
                w.WriteString("l1\nl2\\");
                w.WriteEndObject();
            }
        );
        yield return (
            "long payload above the stack buffer",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteStringProperty("a"u8, new string('x', 300));
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteString(new string('x', 300));
                w.WriteEndObject();
            }
        );
        yield return (
            "second member, so the separator is owed",
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumberProperty("a"u8, 1);
                w.WriteNumberProperty("b"u8, 2);
                w.WriteEndObject();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(1);
                w.WritePropertyName("b"u8);
                w.WriteNumber(2);
                w.WriteEndObject();
            }
        );
        yield return (
            "object element inside a top-level array",
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteStartObject();
                w.WriteNullProperty("a"u8);
                w.WriteEndObject();
                w.WriteEndArray();
            },
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNull();
                w.WriteEndObject();
                w.WriteEndArray();
            }
        );
    }

    // ── number formatting is invariant, and stays the sibling's formatting ──

    [Test]
    public async Task Double_property_is_culture_invariant()
    {
        // de-DE writes 1234,5 and would produce invalid JSON. Pinned so a helper that formatted
        // on its own (instead of delegating to WriteNumber) cannot pass CI on an invariant host.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            await Assert
                .That(
                    Write(
                        (ref JsonWriter w) =>
                        {
                            w.WriteStartObject();
                            w.WriteNumberProperty("v"u8, 1234.5);
                            w.WriteEndObject();
                        }
                    )
                )
                .IsEqualTo("""{"v":1234.5}""");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task Double_property_follows_the_named_literal_policy_like_its_sibling()
    {
        var options = new JsonOptions
        {
            NumberHandling = PicoJetson.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNumberProperty("v"u8, double.NaN);
                        w.WriteEndObject();
                    },
                    options: options
                )
            )
            .IsEqualTo("""{"v":NaN}""");
        await Assert
            .That(
                Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WriteNumberProperty("v"u8, double.PositiveInfinity);
                        w.WriteEndObject();
                    },
                    options: options
                )
            )
            .IsEqualTo("""{"v":Infinity}""");
    }

    [Test]
    public async Task Double_property_rejects_NaN_under_the_strict_policy_exactly_like_its_sibling()
    {
        // Same exception type and message as `WritePropertyName` + `WriteNumber(double)`; the
        // document is abandoned either way, so the helper must not invent a weaker contract.
        var byHelper = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WriteNumberProperty("v"u8, double.NaN);
                w.WriteEndObject();
            }
        );
        var byHand = Throws(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("v"u8);
                w.WriteNumber(double.NaN);
                w.WriteEndObject();
            }
        );
        await Assert.That(byHelper.GetType()).IsEqualTo(byHand.GetType());
        await Assert.That(byHelper.Message).IsEqualTo(byHand.Message);
    }

    private static Exception Throws(WriterAction write)
    {
        var buf = new ArrayBufferWriter<byte>(8);
        return Assert.Throws<ArgumentException>(() =>
        {
            var w = new JsonWriter(buf);
            write(ref w);
        });
    }

    // ── a member needs an object ────────────────────────────────────────────

    [Test]
    public async Task Property_helper_at_the_root_throws_instead_of_writing_a_bare_member()
    {
        // The two-call form emits `"a":null` at depth 0 — not JSON, and silent. A member has no
        // meaning outside an object, so the helper refuses before staging anything.
        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            var buf = new ArrayBufferWriter<byte>(8);
            var w = new JsonWriter(buf);
            w.WriteNullProperty("a"u8);
        });
        await Assert.That(exception.Message).Contains("outside an object");
    }

    [Test]
    public async Task Property_helper_inside_an_array_throws_and_stages_nothing()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var threw = false;
        try
        {
            var w = new JsonWriter(buf);
            w.WriteStartArray();
            w.WriteNumberProperty("a"u8, 1);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(Encoding.UTF8.GetString(buf.WrittenSpan)).IsEqualTo("[");
    }

    // ── the motivating composition: helpers + the scope hook ────────────────

    [Test]
    public async Task Helpers_and_Using_keep_a_partial_document_when_the_scope_exits_early()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        WriteMemberAndReturnEarly(buf);
        await Assert
            .That(Encoding.UTF8.GetString(buf.WrittenSpan))
            .IsEqualTo("""{"name":"ada","id":7""");
    }

    private static void WriteMemberAndReturnEarly(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WriteStringProperty("name"u8, "ada");
        w.WriteNumberProperty("id"u8, 7);
        // no WriteEndObject: the shape that used to silently lose both members
    }
}
