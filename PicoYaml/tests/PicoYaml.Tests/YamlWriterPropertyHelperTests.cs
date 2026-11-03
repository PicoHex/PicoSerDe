namespace PicoYaml.Tests;

/// <summary>
/// "Name and value in one call" for YAML — the same seven members JSON got in phase 1, under the
/// same names, because a member is the same idea in both formats even though the two grammars
/// spell it differently (<c>"a":</c> versus <c>a:</c>).
/// </summary>
/// <remarks>
/// <para>
/// The contract is <b>byte identity</b>, and here it is structural rather than argued: every
/// overload delegates to the existing pair (<see cref="YamlWriter.WritePropertyName(ReadOnlySpan{byte})"/>
/// then <c>WriteX</c>), so quoting, indentation, the <c>- </c> item prefix, the null literal, the
/// space after the colon and the newline are the siblings' — not a second implementation of the
/// same document. There is no new YAML knowledge in this family at all; that is the point.
/// </para>
/// <para>
/// One deliberate difference from JSON's family, and it is a <em>absence</em> rather than a
/// narrowing: these helpers do <b>not</b> require a mapping. JSON refuses <c>"a":</c> at the root
/// because the format cannot hold it; YAML's root <em>is</em> a mapping and <c>a: 1</c> at column 0
/// is a legal one-line document, so a guard here would refuse a document the format can express
/// (rule 5 of the capability rules: "if the format can express it and the framework cannot, that is
/// a debt"). The writer also tracks depth but not container <em>kind</em>, so it has no state that
/// could tell a mapping position from a sequence one — inventing that state would be exactly the
/// "new set of rules" this family exists to avoid.
/// </para>
/// <para>
/// Overload list: <c>string</c> (UTF-8 and UTF-16), <c>int</c>, <c>long</c>, <c>double</c>,
/// <c>bool</c>, <c>null</c> — the same six kinds as JSON's, each with a <c>WriteX</c> sibling to
/// delegate to. No <c>float</c> twin, for JSON's reason: it would widen into <c>double</c> and the
/// printed digits would change quietly.
/// </para>
/// <para>
/// <see cref="YamlWriter"/> is a <c>ref struct</c>, so no writer lives across an <c>await</c>: each
/// test drives one in a synchronous helper and asserts afterwards.
/// </para>
/// </remarks>
public class YamlWriterPropertyHelperTests
{
    private delegate void Write(ref YamlWriter w);

    /// <summary>Runs <paramref name="write"/> on a fresh writer and returns the document as text.</summary>
    private static string Text(Write write, bool indented = false)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf, indented: indented);
        write(ref w);
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>The same bytes, as hex — the evidence form the byte-identity tests compare on.</summary>
    private static string Hex(Write write, bool indented = false)
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var w = new YamlWriter(buf, indented: indented);
        write(ref w);
        return Convert.ToHexString(buf.WrittenSpan);
    }

    // ── the seven overloads, one documented shape each ──────────────────────

    [Test]
    public async Task Null_property_writes_the_null_literal()
    {
        await Assert
            .That(Text((ref YamlWriter w) => w.WriteNullProperty("a"u8)))
            .IsEqualTo("a: null\n");
    }

    [Test]
    public async Task Boolean_property_writes_true_and_false()
    {
        await Assert
            .That(
                Text(
                    (ref YamlWriter w) =>
                    {
                        w.WriteBooleanProperty("a"u8, true);
                        w.WriteBooleanProperty("b"u8, false);
                    }
                )
            )
            .IsEqualTo("a: true\nb: false\n");
    }

    [Test]
    public async Task Int_property_writes_the_extremes_in_the_invariant_culture()
    {
        await Assert
            .That(
                Text(
                    (ref YamlWriter w) =>
                    {
                        w.WriteNumberProperty("min"u8, int.MinValue);
                        w.WriteNumberProperty("max"u8, int.MaxValue);
                    }
                )
            )
            .IsEqualTo("min: -2147483648\nmax: 2147483647\n");
    }

    [Test]
    public async Task Long_property_writes_values_beyond_the_int_range()
    {
        await Assert
            .That(
                Text((ref YamlWriter w) => w.WriteNumberProperty("v"u8, 9_223_372_036_854_775_807L))
            )
            .IsEqualTo("v: 9223372036854775807\n");
    }

    [Test]
    public async Task Double_property_keeps_the_fraction_and_the_point()
    {
        await Assert
            .That(
                Text(
                    (ref YamlWriter w) =>
                    {
                        w.WriteNumberProperty("r"u8, 0.1);
                        w.WriteNumberProperty("w"u8, 1.25);
                    }
                )
            )
            .IsEqualTo("r: 0.1\nw: 1.25\n");
    }

    [Test]
    public async Task String_property_writes_the_utf8_value()
    {
        await Assert
            .That(Text((ref YamlWriter w) => w.WriteStringProperty("name"u8, "Alice"u8)))
            .IsEqualTo("name: Alice\n");
    }

    [Test]
    public async Task String_property_writes_the_utf16_value()
    {
        await Assert
            .That(Text((ref YamlWriter w) => w.WriteStringProperty("name"u8, "Alice")))
            .IsEqualTo("name: Alice\n");
    }

    [Test]
    public async Task String_property_quotes_exactly_where_its_sibling_does()
    {
        // "true" would read back as a bool and "1:2" would split at the colon, so both go out
        // quoted — by NeedsQuoting, through WriteString, not by the helper.
        await Assert
            .That(
                Text(
                    (ref YamlWriter w) =>
                    {
                        w.WriteStringProperty("a"u8, "true"u8);
                        w.WriteStringProperty("b"u8, "1:2"u8);
                    }
                )
            )
            .IsEqualTo("a: \"true\"\nb: \"1:2\"\n");
    }

    [Test]
    public async Task Double_property_rejects_NaN_exactly_like_its_sibling()
    {
        var byHelper = Throws((ref YamlWriter w) => w.WriteNumberProperty("v"u8, double.NaN));
        var byHand = Throws(
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("v"u8);
                w.WriteDouble(double.NaN);
            }
        );

        await Assert.That(byHelper.GetType()).IsEqualTo(byHand.GetType());
        await Assert.That(byHelper.Message).IsEqualTo(byHand.Message);
    }

    private static Exception Throws(Write write)
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new YamlWriter(buf);
        try
        {
            write(ref w);
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("expected the call to throw");
    }

    // ── byte identity with the two-call form ───────────────────────────────

    /// <summary>
    /// The whole point, in one table: for every shape a member can take, the fused call and
    /// <c>WritePropertyName</c> + <c>WriteX</c> produce the <em>same</em> bytes — compared as hex,
    /// so a difference of one space or one newline cannot pass. The cases are chosen to reach
    /// every branch the value writers have: plain and quoted names, a name that starts with the
    /// sequence indicator, a value above the 256-byte stack buffer, a nested mapping, an indented
    /// writer, a member after an empty flow sequence (the <c>_sequenceWroteContent</c> path) and a
    /// member inside a block sequence item.
    /// </summary>
    [Test]
    public async Task EveryOverload_WritesExactlyTheBytesOfTheTwoCallForm()
    {
        var mismatches = new List<string>();
        foreach (var (what, fused, twoCall) in Pairs())
        {
            var fusedHex = Hex(fused);
            var twoCallHex = Hex(twoCall);
            if (fusedHex != twoCallHex)
                mismatches.Add($"{what}: fused={fusedHex} twoCall={twoCallHex}");
        }

        await Assert
            .That(mismatches)
            .IsEmpty()
            .Because("a helper that writes its own bytes is a second dialect of the document");
    }

    private static IEnumerable<(string What, Write Fused, Write TwoCall)> Pairs()
    {
        yield return (
            "string (utf-8)",
            (ref YamlWriter w) => w.WriteStringProperty("name"u8, "Alice"u8),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("name"u8);
                w.WriteString("Alice"u8);
            }
        );
        yield return (
            "string (utf-16)",
            (ref YamlWriter w) => w.WriteStringProperty("name"u8, "Alice"),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("name"u8);
                w.WriteString("Alice");
            }
        );
        yield return (
            "string needing quotes",
            (ref YamlWriter w) => w.WriteStringProperty("a"u8, "yes"u8),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("a"u8);
                w.WriteString("yes"u8);
            }
        );
        yield return (
            "long value above the stack buffer",
            (ref YamlWriter w) => w.WriteStringProperty("big"u8, new string('x', 300)),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("big"u8);
                w.WriteString(new string('x', 300));
            }
        );
        yield return (
            "long name above the stack buffer",
            (ref YamlWriter w) =>
                w.WriteStringProperty(Encoding.UTF8.GetBytes(new string('n', 300)), "v"u8),
            new Write(
                (ref YamlWriter w) =>
                {
                    w.WritePropertyName(Encoding.UTF8.GetBytes(new string('n', 300)));
                    w.WriteString("v"u8);
                }
            )
        );
        yield return (
            "name needing quotes",
            (ref YamlWriter w) => w.WriteNumberProperty("n\nline"u8, 2),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("n\nline"u8);
                w.WriteNumber(2);
            }
        );
        yield return (
            "name starting with the sequence indicator",
            (ref YamlWriter w) => w.WriteNumberProperty("- dash"u8, 6),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("- dash"u8);
                w.WriteNumber(6);
            }
        );
        yield return (
            "int",
            (ref YamlWriter w) => w.WriteNumberProperty("i"u8, -7),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("i"u8);
                w.WriteNumber(-7);
            }
        );
        yield return (
            "long",
            (ref YamlWriter w) => w.WriteNumberProperty("l"u8, -9_223_372_036_854_775_808L),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("l"u8);
                w.WriteInt64(-9_223_372_036_854_775_808L);
            }
        );
        yield return (
            "double",
            (ref YamlWriter w) => w.WriteNumberProperty("d"u8, 1.5),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("d"u8);
                w.WriteDouble(1.5);
            }
        );
        yield return (
            "boolean",
            (ref YamlWriter w) => w.WriteBooleanProperty("b"u8, false),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("b"u8);
                w.WriteBoolean(false);
            }
        );
        yield return (
            "null",
            (ref YamlWriter w) => w.WriteNullProperty("z"u8),
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("z"u8);
                w.WriteNull();
            }
        );
        yield return (
            "member of a nested mapping",
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("outer"u8);
                w.WriteStartMapping();
                w.WriteStringProperty("inner"u8, "v"u8);
                w.WriteEndMapping();
            },
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("outer"u8);
                w.WriteStartMapping();
                w.WritePropertyName("inner"u8);
                w.WriteString("v"u8);
                w.WriteEndMapping();
            }
        );
        yield return (
            "member after an empty flow sequence",
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("items"u8);
                w.WriteStartSequence();
                w.WriteEndSequence();
                w.WriteNullProperty("z"u8);
            },
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("items"u8);
                w.WriteStartSequence();
                w.WriteEndSequence();
                w.WritePropertyName("z"u8);
                w.WriteNull();
            }
        );
        yield return (
            "member inside a block sequence item",
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("rows"u8);
                w.WriteStartSequenceBlock();
                w.WriteNumberProperty("id"u8, 1);
                w.WriteEndSequenceBlock();
            },
            (ref YamlWriter w) =>
            {
                w.WritePropertyName("rows"u8);
                w.WriteStartSequenceBlock();
                w.WritePropertyName("id"u8);
                w.WriteNumber(1);
                w.WriteEndSequenceBlock();
            }
        );
    }

    /// <summary>
    /// No gate, no dialect: the two arms are identical for a document built entirely out of the
    /// fused calls, and identical again when the same document is written indented. This is the
    /// assertion a copy-pasted byte-staging helper would fail (it would not know about the extra
    /// level <c>WriteStartSequenceBlock</c> keeps under a dash).
    /// </summary>
    [Test]
    public async Task AWholeDocumentOfFusedCalls_IsTheSameDocumentInBothIndentModes()
    {
        var fused = Hex(WholeDocumentFused);
        var byHand = Hex(WholeDocumentByHand);
        var fusedIndented = Hex(WholeDocumentFused, indented: true);

        await Assert.That(fused).IsEqualTo(byHand);
        await Assert.That(fusedIndented).IsEqualTo(Hex(WholeDocumentByHand, indented: true));
        await Assert.That(fusedIndented).IsNotEqualTo(fused);
    }

    private static void WholeDocumentFused(ref YamlWriter w)
    {
        w.WriteComment("a person");
        w.WriteStringProperty("name"u8, "Alice");
        w.WriteNumberProperty("age"u8, 30);
        w.WriteNumberProperty("score"u8, 12.5);
        w.WriteBooleanProperty("active"u8, true);
        w.WriteNullProperty("note"u8);
        w.WritePropertyName("tags"u8);
        w.WriteStartSequence();
        w.WriteSequenceItem("one"u8);
        w.WriteSequenceItem("two"u8);
        w.WriteEndSequence();
        w.WritePropertyName("address"u8);
        w.WriteStartMapping();
        w.WriteStringProperty("city"u8, "Paris");
        w.WriteEndMapping();
    }

    private static void WholeDocumentByHand(ref YamlWriter w)
    {
        w.WriteComment("a person");
        w.WritePropertyName("name"u8);
        w.WriteString("Alice");
        w.WritePropertyName("age"u8);
        w.WriteNumber(30);
        w.WritePropertyName("score"u8);
        w.WriteDouble(12.5);
        w.WritePropertyName("active"u8);
        w.WriteBoolean(true);
        w.WritePropertyName("note"u8);
        w.WriteNull();
        w.WritePropertyName("tags"u8);
        w.WriteStartSequence();
        w.WriteSequenceItem("one"u8);
        w.WriteSequenceItem("two"u8);
        w.WriteEndSequence();
        w.WritePropertyName("address"u8);
        w.WriteStartMapping();
        w.WritePropertyName("city"u8);
        w.WriteString("Paris");
        w.WriteEndMapping();
    }

    /// <summary>
    /// A member at the root is a legal YAML document, so the fused call must allow it — the one
    /// place where this family deliberately does less than JSON's, because the format can hold
    /// what JSON cannot.
    /// </summary>
    [Test]
    public async Task RootLevelMember_IsALegalDocument_AndIsNotRefused()
    {
        await Assert
            .That(Text((ref YamlWriter w) => w.WriteStringProperty("a"u8, "1"u8)))
            .IsEqualTo("a: \"1\"\n");
    }

    /// <summary>
    /// Numbers never go through the ambient culture. YAML's siblings format with
    /// <c>InvariantCulture</c>; a helper that formatted on its own would print <c>1,5</c> under
    /// de-DE and change the document's meaning.
    /// </summary>
    [Test]
    public async Task Numbers_are_invariant_under_a_comma_decimal_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var fused = Text((ref YamlWriter w) => w.WriteNumberProperty("d"u8, 1.5));
            var byHand = Text(
                (ref YamlWriter w) =>
                {
                    w.WritePropertyName("d"u8);
                    w.WriteDouble(1.5);
                }
            );
            await Assert.That(fused).IsEqualTo("d: 1.5\n");
            await Assert.That(fused).IsEqualTo(byHand);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
