using System.Globalization;

namespace PicoSerDe.Core.Tests;

/// <summary>
/// <see cref="ScalarCodec.ParseHalf"/> is fail-loud for a text value that does not fit in
/// <see cref="Half"/>: the reader must reject it, never silently substitute ±Infinity.
/// </summary>
/// <remarks>
/// Why this file exists. Deleting the overflow guard (<c>ScalarCodec.cs:175-178</c>) left the
/// <em>whole suite</em> green: the only Half coverage was seven "input longer than the stack
/// buffer" cases, which the <c>ThrowIfTooLong</c> guard rejects before the value is ever looked at.
/// The branch that decides "this number is too big for binary16" was never entered by any test.
///
/// The guard's own shape is the contract, and it has two conjuncts:
/// <c>double.IsFinite(d) &amp;&amp; Half.IsInfinity(h)</c>. The <c>IsFinite</c> half is deliberate —
/// <c>"Infinity"</c> / <c>"NaN"</c> are <em>literals</em> this codec carries on purpose (a Half has
/// an infinity and a NaN, so text that says so is in range), while a finite number that rounds out
/// of range is an error the writer never produces. Both halves are pinned below, so "simplifying"
/// either one goes red.
/// </remarks>
public class ScalarCodecHalfRangeTests
{
    private static Half Parse(string text) => ScalarCodec.ParseHalf(Encoding.UTF8.GetBytes(text));

    private static string Format(Half value)
    {
        Span<byte> dest = stackalloc byte[128];
        if (!ScalarCodec.TryFormatHalf(value, dest, out var n))
            throw new InvalidOperationException($"TryFormatHalf failed for {(double)value:R}.");
        return Encoding.UTF8.GetString(dest[..n]);
    }

    /// <summary>
    /// A finite number outside binary16's range. <c>65504</c> is <see cref="Half.MaxValue"/> and
    /// <c>65520</c> is the round-to-nearest midpoint between it and the next representable step
    /// (65536, which is already infinity), so the boundary sits between the two arguments and the
    /// rounding rule is pinned from both sides in <see cref="AtTheBoundary_RoundsDownToMaxValue"/>.
    /// </summary>
    [Test]
    [Arguments("70000")]
    [Arguments("-70000")]
    [Arguments("1e10")]
    [Arguments("-1e10")]
    [Arguments("65520")]
    [Arguments("-65520")]
    [Arguments("6.6e4")]
    public async Task BeyondHalfRange_ThrowsFormatException(string text) =>
        await Assert.That(() => Parse(text)).Throws<FormatException>();

    /// <summary>
    /// The other side of the boundary: values that round <em>into</em> range must be accepted, and
    /// must land on <see cref="Half.MaxValue"/> rather than being rejected by an over-eager guard.
    /// </summary>
    [Test]
    [Arguments("65504")]
    [Arguments("65504.5")]
    [Arguments("65519.5")]
    public async Task AtTheBoundary_RoundsDownToMaxValue(string text)
    {
        var parsed = Parse(text);

        await Assert.That(Half.IsFinite(parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(Half.MaxValue);
        await Assert.That((double)parsed).IsEqualTo(65504d);
    }

    [Test]
    [Arguments("-65504")]
    [Arguments("-65504.5")]
    [Arguments("-65519.5")]
    public async Task AtTheNegativeBoundary_RoundsUpToMinValue(string text)
    {
        var parsed = Parse(text);

        await Assert.That(Half.IsFinite(parsed)).IsTrue();
        await Assert.That(parsed).IsEqualTo(Half.MinValue);
        await Assert.That((double)parsed).IsEqualTo(-65504d);
    }

    /// <summary>
    /// The <c>IsFinite</c> conjunct: an infinity or NaN spelling is a value a Half can hold, so it
    /// parses. This is the half of the guard that must <em>not</em> become
    /// "any text that converts to a non-finite Half is an error".
    /// </summary>
    [Test]
    [Arguments("Infinity", 1)]
    [Arguments("-Infinity", -1)]
    public async Task InfinityLiteral_IsACarriedValue_NotAnOverflow(string text, int sign)
    {
        var parsed = Parse(text);

        await Assert.That(Half.IsInfinity(parsed)).IsTrue();
        await Assert.That(Math.Sign(Half.Sign(parsed))).IsEqualTo(sign);
    }

    [Test]
    public async Task NaNLiteral_IsACarriedValue_NotAnOverflow()
    {
        await Assert.That(Half.IsNaN(Parse("NaN"))).IsTrue();
    }

    /// <summary>
    /// Inside the range the reader and the writer agree exactly — including ±0, the two subnormal
    /// ends and the values a writer is most likely to emit for a measurement.
    /// </summary>
    public static IEnumerable<(string Name, Half Value)> InRangeValues() =>
        [
            ("Half.MaxValue", Half.MaxValue),
            ("Half.MinValue", Half.MinValue),
            ("Half.Epsilon", Half.Epsilon),
            ("largest subnormal", Half.Epsilon * (Half)1023),
            ("1.5", (Half)1.5),
            ("0", (Half)0),
            ("-0", (Half)(-0d)),
        ];

    [Test]
    [MethodDataSource(nameof(InRangeValues))]
    public async Task InRangeValues_RoundTripExactly(string name, Half value)
    {
        var parsed = Parse(Format(value));

        await Assert.That(parsed).IsEqualTo(value).Because($"{name}: value changed on the wire");
        await Assert
            .That(BitConverter.HalfToUInt16Bits(parsed))
            .IsEqualTo(BitConverter.HalfToUInt16Bits(value))
            .Because($"{name}: the bits are the contract, not just the numeric value");
    }
}
