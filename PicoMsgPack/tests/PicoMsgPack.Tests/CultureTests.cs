using System.Globalization;

namespace PicoMsgPack.Tests;

/// <summary>
/// C-14: decimal / TimeSpan payloads must be culture-invariant on both sides
/// and strictly parsed on the read side.
/// <para>
/// The writer used to emit <c>decimal</c> via <c>ToString()</c>, so de-DE wrote
/// <c>1.5m</c> as <c>"1,5"</c>; the reader used <c>NumberStyles.Any</c>, which
/// treats the comma as a group separator and produced <c>15</c>. Both sides now
/// pin <see cref="CultureInfo.InvariantCulture"/>, and the reader rejects group
/// separators, so such a payload fails loudly instead of silently scaling by 10.
/// </para>
/// <para>
/// Culture is process-wide mutable state and TUnit runs tests in parallel, so
/// every culture-mutating test here opts out with <c>[NotInParallel]</c>.
/// </para>
/// </summary>
public class CultureTests
{
    [Test]
    [NotInParallel]
    public async Task Decimal_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new DecimalDto { Price = 1.5m });

            // The payload itself must carry the invariant form, not "1,5".
            await Assert.That(Encoding.UTF8.GetString(bytes)).Contains("1.5");
            await Assert.That(Encoding.UTF8.GetString(bytes)).DoesNotContain("1,5");

            var back = MsgPackSerializer.Deserialize<DecimalDto>(bytes);
            await Assert.That(back!.Price).IsEqualTo(1.5m);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    [Test]
    [NotInParallel]
    public async Task TimeSpan_IsCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        var expected = new TimeSpan(1, 2, 3, 4) + TimeSpan.FromTicks(5_000_000);
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
                new TimeSpanDto { Duration = expected }
            );

            await Assert.That(Encoding.UTF8.GetString(bytes)).Contains("1.02:03:04.5000000");

            var back = MsgPackSerializer.Deserialize<TimeSpanDto>(bytes);
            await Assert.That(back!.Duration).IsEqualTo(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = old;
        }
    }

    /// <summary>
    /// A payload produced by a culture-sensitive writer ("1,5") must be rejected:
    /// the comma is not a decimal separator under the invariant culture and is no
    /// longer accepted as a group separator either.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task Decimal_CommaFractionPayload_Throws()
    {
        await Assert
            .That(() => MsgPackSerializer.Deserialize<DecimalDto>(MapWithString(0, "1,5")))
            .Throws<FormatException>();
    }

    /// <summary>Group separators are forbidden — "1,234.5" must not read as 1234.5.</summary>
    [Test]
    [NotInParallel]
    public async Task Decimal_GroupSeparatorPayload_Throws()
    {
        await Assert
            .That(() => MsgPackSerializer.Deserialize<DecimalDto>(MapWithString(0, "1,234.5")))
            .Throws<FormatException>();
    }

    /// <summary>Guard against over-tightening: the invariant dotted form still reads.</summary>
    [Test]
    [NotInParallel]
    public async Task Decimal_DottedPayload_Reads()
    {
        var back = MsgPackSerializer.Deserialize<DecimalDto>(MapWithString(0, "-1.5"));
        await Assert.That(back!.Price).IsEqualTo(-1.5m);
    }

    /// <summary>Hand-builds the integer-keyed map <c>{ key: "value" }</c>.</summary>
    private static byte[] MapWithString(int key, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartObject(1);
        mw.WriteInt32(key);
        mw.WriteString(Encoding.UTF8.GetBytes(value));
        mw.WriteEndObject();
        return buffer.WrittenSpan.ToArray();
    }
}

public class DecimalDto
{
    public decimal Price { get; set; }
}

public class TimeSpanDto
{
    public TimeSpan Duration { get; set; }
}
