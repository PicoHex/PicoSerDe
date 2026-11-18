using System.Globalization;

namespace PicoSerDe.Core.Tests;

/// <summary>
/// The <see cref="DateTimeOffset"/> wire contract is a <em>round trip</em>, not a rendering:
/// whatever <see cref="ScalarCodec.TryFormatDateTimeOffset"/> writes,
/// <see cref="ScalarCodec.ParseDateTimeOffset"/> must hand back the same instant
/// <em>and</em> the same offset.
/// </summary>
/// <remarks>
/// Why this file exists. An ablation of <c>ScalarCodec.cs:58</c> — the round-trip pattern
/// <c>"O"</c> replaced by the general pattern <c>"G"</c> — left <b>the whole suite green</b>.
/// The corpus never fed the codec a value whose offset or sub-second tick differs from what the
/// runner's own zone happens to produce, so the branch <c>"O"</c> exists for was never looked at;
/// the gate was green because nothing was asked, not because the answer was right.
///
/// What <c>"G"</c> destroys, and therefore what these cases pin, is exactly two things: the
/// <b>offset</b> (<c>"G"</c> prints none, so a reader has to guess the writer's zone) and the
/// <b>sub-second ticks</b> (<c>"G"</c> stops at whole seconds). Every case below carries at least
/// one of them, so the red-proof does not depend on the machine's <c>TimeZoneInfo</c>: this box is
/// UTC+8 while CI runs UTC, and a test that only pinned <c>+08:00</c> would be green here and red
/// there (AGENTS.md, "A test must not encode the environment it happens to run in").
///
/// <see cref="DateTimeOffset.Equals(DateTimeOffset)"/> compares the instant only, so the offset is
/// asserted separately: with <c>Equals</c> alone, a value re-read in the reader's local zone would
/// look preserved.
/// </remarks>
public class ScalarCodecDateTimeOffsetRoundTripTests
{
    // 64 chars is the codec's internal stack buffer (ScalarCodec.MaxChars); "O" needs 33.
    private const int WireBufferSize = 128;

    /// <summary>
    /// The worst values for a round trip: both non-zero offset signs, a 7-digit tick fraction, the
    /// two ends of the representable range, and the extreme clock times carrying a non-zero offset
    /// (the shape where the UTC instant and the printed clock time sit at opposite ends of the
    /// range, which is where an offset-aware parser gives up first).
    /// </summary>
    public static IEnumerable<(string Name, DateTimeOffset Value)> WorstCases() =>
        [
            (
                "+08:00 with 7-digit ticks",
                new DateTimeOffset(2024, 2, 29, 13, 45, 7, TimeSpan.FromHours(8)).AddTicks(1_234_567)
            ),
            (
                "-03:30 with 7-digit ticks",
                new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromMinutes(-210))
                    .AddTicks(9_999_999)
            ),
            (
                "UTC with a single tick",
                new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1)
            ),
            ("DateTimeOffset.MinValue", DateTimeOffset.MinValue),
            ("DateTimeOffset.MaxValue", DateTimeOffset.MaxValue),
            ("max clock time at +08:00", new DateTimeOffset(DateTime.MaxValue, TimeSpan.FromHours(8))),
            (
                "min clock time at -03:30",
                new DateTimeOffset(DateTime.MinValue, TimeSpan.FromMinutes(-210))
            ),
        ];

    /// <summary>Runs the writer under test and decodes what it put on the wire.</summary>
    private static string Format(DateTimeOffset value)
    {
        Span<byte> dest = stackalloc byte[WireBufferSize];
        if (!ScalarCodec.TryFormatDateTimeOffset(value, dest, out var n))
            throw new InvalidOperationException($"TryFormatDateTimeOffset failed for {value:O}.");
        return Encoding.UTF8.GetString(dest[..n]);
    }

    private static DateTimeOffset Parse(string wire) =>
        ScalarCodec.ParseDateTimeOffset(Encoding.UTF8.GetBytes(wire));

    [Test]
    [MethodDataSource(nameof(WorstCases))]
    public async Task FormatThenParse_HoldsTheInstantTheOffsetAndTheTicks(
        string name,
        DateTimeOffset value
    )
    {
        var parsed = Parse(Format(value));

        // The instant.
        await Assert.That(parsed).IsEqualTo(value).Because($"{name}: the instant moved");
        // The offset — what "G" drops first, and what Equals above cannot see.
        await Assert.That(parsed.Offset).IsEqualTo(value.Offset).Because($"{name}: offset lost");
        // The wall clock down to the last tick — the second thing "G" drops.
        await Assert
            .That(parsed.Ticks)
            .IsEqualTo(value.Ticks)
            .Because($"{name}: sub-second ticks lost");
        // Strongest form: nothing about the value changed, so the text cannot change either.
        await Assert
            .That(Format(parsed))
            .IsEqualTo(Format(value))
            .Because($"{name}: the wire text is not stable");
    }

    /// <summary>
    /// The same round trip read from the other side: the text is asserted against the ISO-8601
    /// round-trip literal spelled out by hand, never against anything the codec computed. That is
    /// what makes <c>"G"</c> impossible to mistake for a formatting detail — the wire form is a
    /// published contract (<c>yyyy-MM-ddTHH:mm:ss.fffffffK</c>), and an independent reader of the
    /// format has to recover both halves of it.
    /// </summary>
    [Test]
    [Arguments("2024-02-29T13:45:07.1234567+08:00")]
    [Arguments("1999-12-31T23:59:59.9999999-03:30")]
    [Arguments("2024-01-01T00:00:00.0000001+00:00")]
    [Arguments("0001-01-01T00:00:00.0000000+00:00")]
    [Arguments("9999-12-31T23:59:59.9999999+00:00")]
    public async Task WireTextIsTheRoundTripLiteral_AndParsesBackToIt(string wire)
    {
        // Parse the literal by hand first: this test must not lean on the codec's own formatting
        // to build its expectation.
        var expected = DateTimeOffset.Parse(
            wire,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind
        );

        await Assert
            .That(Format(expected))
            .IsEqualTo(wire)
            .Because("the writer emits the round-trip literal");
        await Assert
            .That(Parse(wire))
            .IsEqualTo(expected)
            .Because("the reader gives the instant back");
        // The offset half of the literal survives the reader's direction as well.
        await Assert
            .That(Parse(wire).Offset)
            .IsEqualTo(expected.Offset)
            .Because("the reader gives the offset back");
    }

    /// <summary>
    /// An offset-blind writer collapses two different instants onto the same text. They must not
    /// collapse here — and each text must read back as its own instant, so the difference is real
    /// rather than cosmetic.
    /// </summary>
    [Test]
    public async Task TwoValuesDifferingOnlyByOffset_KeepDistinctWireForms()
    {
        var shifted = new DateTimeOffset(2024, 2, 29, 13, 45, 7, TimeSpan.FromHours(8))
            .AddTicks(1_234_567);
        var utc = shifted.ToUniversalTime();

        await Assert.That(Format(shifted)).IsNotEqualTo(Format(utc));
        await Assert.That(Parse(Format(shifted)).UtcDateTime).IsEqualTo(shifted.UtcDateTime);
        await Assert.That(Parse(Format(utc)).UtcDateTime).IsEqualTo(utc.UtcDateTime);
        await Assert.That(Parse(Format(shifted)).Offset).IsEqualTo(TimeSpan.FromHours(8));
        await Assert.That(Parse(Format(utc)).Offset).IsEqualTo(TimeSpan.Zero);
    }
}
