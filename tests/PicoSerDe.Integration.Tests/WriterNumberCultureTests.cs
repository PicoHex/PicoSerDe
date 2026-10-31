using System.Globalization;

namespace PicoSerDe.Integration.Tests;

// ── The write side may not borrow the machine's number spelling ──────────────────────
//
// Cross-format audit item C7: the INI/TOML/YAML writers formatted INTEGERS with no
// IFormatProvider (`value.TryFormat(buf, out var w)`, whose third parameter defaults to null
// ⇒ CultureInfo.CurrentCulture) while the same three classes formatted their `double` and
// `decimal` members with an explicit `CultureInfo.InvariantCulture`. One number, two
// cultures, one file.
//
// The symptom is not cosmetic. `sv-SE` does not spell the minus sign as ASCII 0x2D: it uses
// U+2212 MINUS SIGN, so a `-42` written on a Swedish-locale machine was five bytes
// (2D 34 32 twice over: `−42`) that no other reader — including this library's own reader on
// any other machine — resolves as a negative number. `fr-FR` turns `1.5` into `1,5`, which
// the audit did not catch for the integer path (integers have no separator) but which is the
// same defect one class over: the day someone copies the integer call into the float path,
// the separator follows the machine. This is the same class of bug that was fixed once
// already in `BenchProtocol.FormatCell` (`b0985d1`), and `AGENTS.md` names it a working rule:
// a test must not encode the environment it happens to run in, a writer must not either.
//
// What is asserted here is the property the wire format actually needs — "the same bytes in
// every culture" — not a snapshot of one machine's output. The oracle for the byte-level
// claim is the invariant-culture serialization of the same value; the oracle for the
// human-readable claim is the ASCII minus sign and the ASCII full stop, spelled as literals.
//
// [NotInParallel]: this class moves process-wide culture state. Every culture is restored in
// a `finally` (the precedent is `tests/PicoBench.Protocol.Tests/ProtocolCultureTests.cs`).

/// <summary>
/// The smallest model that reaches every numeric write path: the integer overloads
/// (<c>WriteKeyValue(…, int)</c> / <c>(…, long)</c> in INI and TOML, <c>WriteNumber</c> /
/// <c>WriteInt64</c> in YAML), the <c>double</c> path (<c>fr-FR</c>'s comma), and
/// <c>decimal</c> (both the sign and the separator).
/// </summary>
[PicoSerializable]
public class NegativeNumberModel
{
    public int Int { get; set; }
    public long Long { get; set; }
    public float Float { get; set; }
    public double Double { get; set; }
    public decimal Decimal { get; set; }
}

/// <summary>
/// Culture invariants of the four text writers plus the binary one (which has none to break).
/// </summary>
[NotInParallel]
public class WriterNumberCultureTests
{
    /// <summary>U+2212 MINUS SIGN — what <c>sv-SE</c> (and only <c>sv-SE</c> here) uses.</summary>
    private const string NonAsciiMinusSign = "\u2212";

    /// <summary>What has to be on the wire for a negative integer, in every culture.</summary>
    private const string AsciiMinusSign = "-";

    private static readonly CultureInfo Swedish = new("sv-SE");
    private static readonly CultureInfo French = new("fr-FR");

    /// <summary>The same value in every format, driven with negative members on purpose.</summary>
    private static NegativeNumberModel Create() =>
        new()
        {
            Int = -42,
            Long = -9_876_543_210L,
            Float = -2.5f,
            Double = -1.5,
            Decimal = -1234.5m,
        };

    /// <summary>The four text formats, by the bytes their serializer produces.</summary>
    private static readonly (
        string Format,
        Func<NegativeNumberModel, byte[]> Write
    )[] TextFormats =
    [
        ("ini", m => IniSerializer.SerializeToUtf8Bytes(m)),
        ("toml", m => TomlSerializer.SerializeToUtf8Bytes(m)),
        ("yaml", m => YamlSerializer.SerializeToUtf8Bytes(m)),
        ("json", m => JsonSerializer.SerializeToUtf8Bytes(m)),
    ];

    private static string Text(Func<NegativeNumberModel, byte[]> write) =>
        Encoding.UTF8.GetString(write(Create()));

    // ── the oracle itself ────────────────────────────────────────────────────────────

    /// <summary>
    /// The two cultures under test must really be hostile, or every assertion below passes for
    /// the wrong reason: <c>sv-SE</c>'s negative sign must not be ASCII and <c>fr-FR</c>'s
    /// decimal separator must be a comma. If a future ICU changes either one this fails
    /// loudly here rather than silently turning the rest of the class into a no-op.
    /// </summary>
    [Test]
    public async Task The_cultures_under_test_really_differ_from_invariant()
    {
        await Assert.That(Swedish.NumberFormat.NegativeSign).IsEqualTo(NonAsciiMinusSign);
        await Assert.That(French.NumberFormat.NegativeSign).IsEqualTo(AsciiMinusSign);
        await Assert.That(French.NumberFormat.NumberDecimalSeparator).IsEqualTo(",");
        await Assert
            .That(CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator)
            .IsEqualTo(".");
    }

    // ── the defect: one test per affected format ────────────────────────────────────

    /// <summary>
    /// INI: the integer overloads (<c>IniWriter.WriteKeyValue(string, int)</c>,
    /// <c>WriteKeyValue(string, long)</c>, and their UTF-8-key twins) took the ambient culture.
    /// </summary>
    [Test]
    public async Task Ini_writes_the_ascii_minus_sign_under_the_swedish_culture()
    {
        var wire = InCulture(Swedish, () => Text(m => IniSerializer.SerializeToUtf8Bytes(m)));

        await Assert.That(wire).Contains("= " + AsciiMinusSign + "42");
        await Assert.That(wire).Contains(AsciiMinusSign + "9876543210");
        await Assert.That(wire.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// TOML: <c>WriteKeyValue(ReadOnlySpan&lt;byte&gt;, int/long/ulong)</c> and the array
    /// value overloads (the latter reached through <c>int[]</c>/<c>List&lt;long&gt;</c>).
    /// </summary>
    [Test]
    public async Task Toml_writes_the_ascii_minus_sign_under_the_swedish_culture()
    {
        var wire = InCulture(Swedish, () => Text(m => TomlSerializer.SerializeToUtf8Bytes(m)));

        await Assert.That(wire).Contains("= " + AsciiMinusSign + "42");
        await Assert.That(wire).Contains(AsciiMinusSign + "9876543210");
        await Assert.That(wire.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// YAML: <c>YamlWriter.WriteNumber(int)</c>, <c>WriteInt64</c> and <c>WriteUInt64</c> —
    /// the bytes the task's audit named at <c>YamlWriter.cs:167/240/254</c>.
    /// </summary>
    [Test]
    public async Task Yaml_writes_the_ascii_minus_sign_under_the_swedish_culture()
    {
        var wire = InCulture(Swedish, () => Text(m => YamlSerializer.SerializeToUtf8Bytes(m)));

        await Assert.That(wire).Contains(AsciiMinusSign + "42");
        await Assert.That(wire).Contains(AsciiMinusSign + "9876543210");
        await Assert.That(wire.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
    }

    /// <summary>
    /// JSON is the control that says the defect is a WRITER defect and not a framework one:
    /// <c>JsonWriter.WriteFormattable</c> already passed <c>CultureInfo.InvariantCulture</c>,
    /// so it is green before the fix as well as after it.
    /// </summary>
    [Test]
    public async Task Json_writes_the_ascii_minus_sign_under_the_swedish_culture()
    {
        var wire = InCulture(Swedish, () => Text(m => JsonSerializer.SerializeToUtf8Bytes(m)));

        await Assert.That(wire).Contains(AsciiMinusSign + "42");
        await Assert.That(wire.Contains(NonAsciiMinusSign, StringComparison.Ordinal)).IsFalse();
    }

    // ── the property, not one culture's spelling ────────────────────────────────────

    /// <summary>
    /// <b>The core assertion.</b> The wire bytes are a function of the VALUE, not of the
    /// culture the process happens to run under: serializing the same model under
    /// <c>sv-SE</c> (U+2212 minus sign) and under <c>fr-FR</c> (comma decimal separator) must
    /// produce exactly the bytes the invariant culture produces. This covers the integer
    /// paths (sign) and the <c>double</c>/<c>decimal</c> paths (separator) in one claim, for
    /// all four text formats.
    /// </summary>
    /// <param name="format">Which text format.</param>
    [Test]
    [Arguments("ini")]
    [Arguments("toml")]
    [Arguments("yaml")]
    [Arguments("json")]
    public async Task The_wire_bytes_do_not_depend_on_the_ambient_culture(string format)
    {
        var write = Array.Find(TextFormats, f => f.Format == format).Write;
        var invariant = InCulture(CultureInfo.InvariantCulture, () => write(Create()));

        foreach (var culture in new[] { Swedish, French })
        {
            var actual = InCulture(culture, () => write(Create()));
            await Assert
                .That(Encoding.UTF8.GetString(actual))
                .IsEqualTo(Encoding.UTF8.GetString(invariant))
                .Because(
                    $"{format} under {culture.Name} must be byte-identical to the invariant "
                        + "serialization of the same value"
                );
        }

        // The decimal separator has to be the invariant one in the text, whatever the culture
        // did: `-1.5`, not `-1,5` (fr-FR) and not `−1.5` (sv-SE).
        var text = Encoding.UTF8.GetString(invariant);
        await Assert.That(text).Contains(AsciiMinusSign + "1.5");
        await Assert.That(text).Contains(AsciiMinusSign + "1234.5");
    }

    /// <summary>
    /// MessagePack is the control for "a binary format cannot have this bug": its numbers are
    /// two's-complement bytes, so the ambient culture has nothing to format. Included so the
    /// report can say the fifth writer was checked rather than assumed.
    /// </summary>
    [Test]
    public async Task MsgPack_bytes_do_not_depend_on_the_ambient_culture()
    {
        var invariant = InCulture(
            CultureInfo.InvariantCulture,
            () => MsgPackSerializer.SerializeToUtf8Bytes(Create())
        );

        foreach (var culture in new[] { Swedish, French })
        {
            var actual = InCulture(culture, () => MsgPackSerializer.SerializeToUtf8Bytes(Create()));
            await Assert.That(actual.AsSpan().SequenceEqual(invariant)).IsTrue();
        }
    }

    // ── culture funnel ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="action"/> with <see cref="CultureInfo.CurrentCulture"/> and
    /// <see cref="CultureInfo.CurrentUICulture"/> set to <paramref name="culture"/>, and puts
    /// both back afterwards — including when the action throws, so a red test cannot leave the
    /// rest of the assembly formatting under <c>sv-SE</c>.
    /// </summary>
    private static T InCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }
}
