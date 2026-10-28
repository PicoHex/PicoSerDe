using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// The number token — separator, digits and cursor update — is staged as one write behind one
/// <c>Ensure</c> instead of four calls that each re-read the writer's fields.
/// </summary>
/// <remarks>
/// <para>
/// No byte may move while the plumbing changes, so this file is an equivalence proof rather
/// than a behaviour test. It covers the three ways the fused write can be wrong:
/// </para>
/// <list type="bullet">
///   <item><b>the separator</b>: a missing comma, a doubled comma, or one written for the first
///   element of a container (the "is a comma needed" decision now shares the same staged region
///   as the digits, so it has to be made before the span cursor moves);</item>
///   <item><b>the digits</b>: every overload formats through <c>IUtf8SpanFormattable.TryFormat</c>
///   with the invariant provider, and the invariant text of a <c>double</c>/<c>decimal</c> has
///   more shapes (<c>-0</c>, exponents, 28 significant digits) than an integer ever does;</item>
///   <item><b>the reservation</b>: the token asks for one byte more than the value's worst case
///   (the separator), and a number that outgrows the reservation would be formatted into a
///   short span — the <c>TryFormat</c> answer is ignored there, so the bound itself is what
///   has to be right. The widest value of each type is asserted at every span boundary.</item>
/// </list>
/// <para>
/// Every expectation is either a hand-written literal or the value's own
/// <c>ToString(CultureInfo.InvariantCulture)</c> — the text the old per-overload code produced
/// by construction.
/// </para>
/// </remarks>
public class JsonWriterNumberTokenTests
{
    private delegate void WriterAction(ref JsonWriter w);

    private static string Write(WriterAction write) =>
        Write(write, new ArrayBufferWriter<byte>(8), out _);

    private static string Write(
        WriterAction write,
        IBufferWriter<byte> buffer,
        out long bytesWritten
    )
    {
        var w = new JsonWriter(buffer);
        write(ref w);
        bytesWritten = w.BytesWritten;
        var span = buffer is ArrayBufferWriter<byte> ab
            ? ab.WrittenSpan
            : ((TinyChunkBufferWriter)buffer).WrittenSpan;
        return Encoding.UTF8.GetString(span);
    }

    // ── the digits, per overload, against the invariant text of the value ──

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(-1)]
    [Arguments(int.MaxValue)]
    [Arguments(int.MinValue)]
    [Arguments(1234567)]
    [Arguments(-1234567)]
    [Arguments(1000000000)]
    public async Task Int32WritesItsInvariantText(int value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(0L)]
    [Arguments(-1L)]
    [Arguments(long.MaxValue)]
    [Arguments(long.MinValue)]
    public async Task Int64WritesItsInvariantText(long value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(0UL)]
    [Arguments(ulong.MaxValue)]
    [Arguments(long.MaxValue + 1UL)]
    public async Task UInt64WritesItsInvariantText(ulong value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments("0")]
    [Arguments("1")]
    [Arguments("-1")]
    [Arguments("149.99")]
    [Arguments("1.0000001")]
    [Arguments("1.5000")]
    [Arguments("10000")]
    [Arguments("79228162514264337593543950335")]
    [Arguments("-79228162514264337593543950335")]
    [Arguments("0.0000000000000000000000000001")]
    public async Task DecimalWritesItsInvariantText(string literal)
    {
        // Parsed from its own text so the trailing zeros and the scale survive: 1.5000m must
        // keep four decimals on the wire, which is exactly what ToString/ TryFormat agree on.
        var value = decimal.Parse(literal, CultureInfo.InvariantCulture);
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture))
            .Because(literal);
    }

    [Test]
    [Arguments(0.0)]
    [Arguments(-0.0)]
    [Arguments(1.5)]
    [Arguments(4.5)]
    [Arguments(double.MaxValue)]
    [Arguments(double.MinValue)]
    [Arguments(double.Epsilon)]
    [Arguments(2.2250738585072014E-308)]
    [Arguments(1e21)]
    [Arguments(1e-7)]
    [Arguments(100.0)]
    public async Task DoubleWritesItsInvariantText(double value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture))
            .Because(value.ToString("R", CultureInfo.InvariantCulture));

    [Test]
    [Arguments(0f)]
    [Arguments(-0f)]
    [Arguments(0.1f)]
    [Arguments(float.MaxValue)]
    [Arguments(float.MinValue)]
    [Arguments(float.Epsilon)]
    public async Task SingleWritesItsInvariantText(float value) =>
        // A float must keep its own precision: widening it to double would expose the binary
        // expansion (0.1f would become 0.10000000149011612).
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(short.MinValue)]
    [Arguments(short.MaxValue)]
    [Arguments((short)0)]
    public async Task Int16WritesItsInvariantText(short value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(ushort.MinValue)]
    [Arguments(ushort.MaxValue)]
    public async Task UInt16WritesItsInvariantText(ushort value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(sbyte.MinValue)]
    [Arguments(sbyte.MaxValue)]
    [Arguments((sbyte)0)]
    public async Task SByteWritesItsInvariantText(sbyte value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(byte.MinValue)]
    [Arguments(byte.MaxValue)]
    public async Task ByteWritesItsInvariantText(byte value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    [Test]
    [Arguments(0u)]
    [Arguments(uint.MaxValue)]
    [Arguments(1234567890u)]
    public async Task UInt32WritesItsInvariantText(uint value) =>
        await Assert
            .That(Write((ref JsonWriter w) => w.WriteNumber(value)))
            .IsEqualTo(value.ToString(CultureInfo.InvariantCulture));

    // ── the separator: first element, middle elements, nested, and after a property name ──

    [Test]
    public async Task ArrayElementsAreCommaSeparatedWithNoLeadingOrTrailingComma()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteNumber(1);
                w.WriteNumber(-2);
                w.WriteNumber(3);
                w.WriteEndArray();
            }
        );
        await Assert.That(json).IsEqualTo("[1,-2,3]");
    }

    [Test]
    public async Task AnEmptyArrayHasNoComma()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteEndArray();
            }
        );
        await Assert.That(json).IsEqualTo("[]");
    }

    [Test]
    public async Task NestedArraysSeparateAtBothDepths()
    {
        // The comma bookkeeping is a bitmask over depth: a number inside the inner array must
        // not inherit the outer array's "one value written" bit, and the outer array must not
        // emit a second comma once the inner one closes.
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteNumber(1);
                w.WriteStartArray();
                w.WriteNumber(2);
                w.WriteNumber(3);
                w.WriteEndArray();
                w.WriteNumber(4);
                w.WriteEndArray();
            }
        );
        await Assert.That(json).IsEqualTo("[1,[2,3],4]");
    }

    [Test]
    public async Task APropertyValueGetsNoSeparatorOfItsOwn()
    {
        // A number written straight after a property name must not emit a comma: the separator
        // belongs to the member, and the property name already staged it.
        var json = Write(
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
        await Assert.That(json).IsEqualTo("""{"a":1,"b":2}""");
    }

    [Test]
    public async Task NumbersMixWithOtherValueTokensWithoutLosingAComma()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                w.WriteNumber(1);
                w.WriteString("two"u8);
                w.WriteBoolean(true);
                w.WriteNull();
                w.WriteNumber(2.5);
                w.WriteEndArray();
            }
        );
        await Assert.That(json).IsEqualTo("""[1,"two",true,null,2.5]""");
    }

    [Test]
    public async Task NonFiniteNumbersStillFollowTheNumberHandlingPolicy()
    {
        // The fused body sits behind the NaN/Infinity arms: those must still write their named
        // literals — and, crucially, still write the separator exactly once before them.
        var buffer = new ArrayBufferWriter<byte>(8);
        var options = new JsonOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        var w = new JsonWriter(buffer, options: options);
        w.WriteStartArray();
        w.WriteNumber(double.NaN);
        w.WriteNumber(double.PositiveInfinity);
        w.WriteNumber(double.NegativeInfinity);
        w.WriteNumber(1.0);
        w.WriteEndArray();
        await Assert
            .That(Encoding.UTF8.GetString(buffer.WrittenSpan))
            .IsEqualTo("[NaN,Infinity,-Infinity,1]");
    }

    [Test]
    public async Task NaNIsRejectedByDefault()
    {
        await Assert
            .That(() => Write((ref JsonWriter w) => w.WriteNumber(double.NaN)))
            .Throws<ArgumentException>();
        await Assert
            .That(() => Write((ref JsonWriter w) => w.WriteNumber(float.NaN)))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task InfinityIsRejectedByDefault()
    {
        await Assert
            .That(() => Write((ref JsonWriter w) => w.WriteNumber(double.PositiveInfinity)))
            .Throws<ArgumentException>();
        await Assert
            .That(() => Write((ref JsonWriter w) => w.WriteNumber(float.NegativeInfinity)))
            .Throws<ArgumentException>();
    }

    // ── the reservation: the widest value of each type, at every span boundary ──

    [Test]
    public async Task TheWidestTokenOfEveryOverloadSurvivesEverySpanBoundary()
    {
        (string Name, string Expected, WriterAction Write)[] cases =
        [
            ("int min", "-2147483648", (ref JsonWriter w) => w.WriteNumber(int.MinValue)),
            ("int max", "2147483647", (ref JsonWriter w) => w.WriteNumber(int.MaxValue)),
            (
                "long min",
                "-9223372036854775808",
                (ref JsonWriter w) => w.WriteNumber(long.MinValue)
            ),
            ("long max", "9223372036854775807", (ref JsonWriter w) => w.WriteNumber(long.MaxValue)),
            (
                "ulong max",
                "18446744073709551615",
                (ref JsonWriter w) => w.WriteNumber(ulong.MaxValue)
            ),
            (
                "decimal min",
                "-79228162514264337593543950335",
                (ref JsonWriter w) => w.WriteNumber(decimal.MinValue)
            ),
            (
                "decimal max",
                "79228162514264337593543950335",
                (ref JsonWriter w) => w.WriteNumber(decimal.MaxValue)
            ),
            (
                "double max",
                "1.7976931348623157E+308",
                (ref JsonWriter w) => w.WriteNumber(double.MaxValue)
            ),
            (
                "double min",
                "-1.7976931348623157E+308",
                (ref JsonWriter w) => w.WriteNumber(double.MinValue)
            ),
            ("float min", "-3.4028235E+38", (ref JsonWriter w) => w.WriteNumber(float.MinValue)),
            ("short min", "-32768", (ref JsonWriter w) => w.WriteNumber(short.MinValue)),
            ("ushort max", "65535", (ref JsonWriter w) => w.WriteNumber(ushort.MaxValue)),
            ("sbyte min", "-128", (ref JsonWriter w) => w.WriteNumber(sbyte.MinValue)),
            ("byte max", "255", (ref JsonWriter w) => w.WriteNumber(byte.MaxValue)),
            ("uint max", "4294967295", (ref JsonWriter w) => w.WriteNumber(uint.MaxValue)),
        ];

        foreach (var (name, expected, write) in cases)
        {
            // A separator is part of the token when the value is not the first element, so the
            // widest *token* is the widest value plus one byte.
            var token = expected.Length + 1;
            foreach (
                var capacity in new[]
                {
                    1,
                    2,
                    3,
                    token - 2,
                    token - 1,
                    token,
                    token + 1,
                    token + 15,
                    token + 16,
                    256,
                    257,
                }
            )
            {
                if (capacity < 1)
                    continue;
                var label = $"{name} at first-chunk capacity {capacity}";
                var json = Write(
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartArray();
                        w.WriteNumber(0);
                        write(ref w);
                        w.WriteEndArray();
                    },
                    new TinyChunkBufferWriter(capacity),
                    out var bytesWritten
                );
                await Assert.That(json).IsEqualTo($"[0,{expected}]").Because(label);
                await Assert
                    .That(bytesWritten)
                    .IsEqualTo((long)Encoding.UTF8.GetByteCount(json))
                    .Because(label + ": BytesWritten");
            }
        }
    }
}
