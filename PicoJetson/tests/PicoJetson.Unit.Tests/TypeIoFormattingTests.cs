using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>
/// The temporal family (DateTime, DateTimeOffset, DateOnly, TimeOnly, TimeSpan) and Guid are
/// written as a quoted JSON string formatted straight into UTF-8, instead of
/// <c>value.ToString(...)</c> followed by an encode.
/// </summary>
/// <remarks>
/// <para>
/// The generator used to emit <c>var __iso = value.ToString("O"); jw.WriteString(__iso);</c> —
/// one string per value. Measuring the Complex POCO row showed those two members (DateTime 21%
/// of the document, Guid 14%) ahead of every other cost, and part of that cost was the
/// allocation (.superpowers/sdd/json-elemwrite/report.md). The digits are now produced by the
/// value's own <c>TryFormat</c>, so the interesting question is not "does it look right" but
/// "is it the same bytes": every expectation here is <c>ToString</c> of the same value under the
/// same culture, i.e. the text the old path produced by construction.
/// </para>
/// <para>
/// The <c>DateTimeKind</c> and UTC-offset spectrum is the reason this file is long: "O" is the
/// round-trip pattern, and it appends nothing for <c>Unspecified</c>, <c>Z</c> for
/// <c>Utc</c> and a signed <c>±HH:mm</c> for <c>Local</c> — three different suffixes that must
/// survive the change in formatting route, plus the extremes at which any hand-rolled date
/// writer would break.
/// </para>
/// </remarks>
public class TypeIoFormattingTests
{
    private delegate void WriterAction(ref JsonWriter w);

    private static string Write(WriterAction write)
    {
        var buffer = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buffer);
        write(ref w);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ── DateTime ──

    public static IEnumerable<(string Name, DateTime Value)> DateTimeCases() =>
        [
            ("utc midnight", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            (
                "utc with ticks",
                new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567)
            ),
            (
                "utc max ticks",
                new DateTime(2026, 10, 27, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999)
            ),
            ("unspecified", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Unspecified)),
            ("local", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Local)),
            (
                "local max ticks",
                new DateTime(2026, 10, 27, 23, 59, 59, DateTimeKind.Local).AddTicks(9999999)
            ),
            ("min value", DateTime.MinValue),
            ("max value", DateTime.MaxValue),
            ("min utc", new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            ("max utc", new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999)),
            ("leap day", new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc)),
            (
                "seven digit fraction",
                new DateTime(2000, 1, 1, 1, 2, 3, DateTimeKind.Utc).AddTicks(4500000)
            ),
            ("one tick", new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1)),
        ];

    [Test]
    [MethodDataSource(nameof(DateTimeCases))]
    public async Task DateTimeIsItsRoundTripText(string name, DateTime value)
    {
        var expected = value.ToString("O", CultureInfo.InvariantCulture);
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, "O")))
            .IsEqualTo($"\"{expected}\"")
            .Because($"DateTime {name} ({value.Kind})");
    }

    public static IEnumerable<(string Name, DateTime Value)> FormattedDateTimeCases() =>
        [
            ("date only", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc)),
            ("compact", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified)),
        ];

    [Test]
    [MethodDataSource(nameof(FormattedDateTimeCases))]
    public async Task ACustomDateTimeFormatIsItsToStringText(string name, DateTime value)
    {
        // The attribute path keeps the ToString(string) route (an arbitrary pattern could
        // outgrow the helper's buffer); both routes must agree with it.
        foreach (var format in new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss", "'at' HH:mm" })
        {
            var expected = value.ToString(format, CultureInfo.InvariantCulture);
            await Assert
                .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, format)))
                .IsEqualTo($"\"{expected}\"")
                .Because($"{name} as '{format}'");
        }
    }

    // ── DateTimeOffset ──

    public static IEnumerable<(string Name, DateTimeOffset Value)> DateTimeOffsetCases() =>
        [
            ("utc", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)),
            ("plus eight", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(8))),
            ("minus five", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromHours(-5))),
            (
                "plus five thirty",
                new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.FromMinutes(330))
            ),
            ("ticks", new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567)),
            ("min value", DateTimeOffset.MinValue),
            ("max value", DateTimeOffset.MaxValue),
        ];

    [Test]
    [MethodDataSource(nameof(DateTimeOffsetCases))]
    public async Task DateTimeOffsetIsItsRoundTripText(string name, DateTimeOffset value)
    {
        var expected = value.ToString("O", CultureInfo.InvariantCulture);
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteDateTimeOffset(ref w, value)))
            .IsEqualTo($"\"{expected}\"")
            .Because($"DateTimeOffset {name}");
    }

    // ── Guid ──

    public static IEnumerable<(string Name, Guid Value)> GuidCases() =>
        [
            ("empty", Guid.Empty),
            ("all ones", Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")),
            ("benchmark id", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")),
            ("uppercase input", Guid.Parse("DEADBEEF-CAFE-BABE-F00D-0123456789AB")),
            ("leading zeros", Guid.Parse("00000000-0000-0000-0000-000000000001")),
            ("one", Guid.Parse("10000000-0000-0000-0000-000000000000")),
            ("bytes high", Guid.Parse("01020304-0506-0708-090a-0b0c0d0e0f10")),
        ];

    [Test]
    [MethodDataSource(nameof(GuidCases))]
    public async Task GuidIsItsDText(string name, Guid value)
    {
        // "D" is the default ToString form; the uppercase input above proves the output is
        // lower case, which is what the reader accepts and what the old path emitted.
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, "D")))
            .IsEqualTo($"\"{value.ToString("D", CultureInfo.InvariantCulture)}\"")
            .Because($"Guid {name}");
    }

    // ── DateOnly / TimeOnly / TimeSpan ──

    public static IEnumerable<(string Name, DateOnly Value)> DateOnlyCases() =>
        [
            ("min", DateOnly.MinValue),
            ("max", DateOnly.MaxValue),
            ("leap day", new DateOnly(2024, 2, 29)),
            ("today-ish", new DateOnly(2026, 10, 27)),
        ];

    [Test]
    [MethodDataSource(nameof(DateOnlyCases))]
    public async Task DateOnlyIsItsRoundTripText(string name, DateOnly value) =>
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, "O")))
            .IsEqualTo($"\"{value.ToString("O", CultureInfo.InvariantCulture)}\"")
            .Because($"DateOnly {name}");

    public static IEnumerable<(string Name, TimeOnly Value)> TimeOnlyCases() =>
        [
            ("min", TimeOnly.MinValue),
            ("max", TimeOnly.MaxValue),
            ("noon", new TimeOnly(12, 0)),
            ("seven digits", new TimeOnly(13, 14, 15).Add(TimeSpan.FromTicks(1234567))),
        ];

    [Test]
    [MethodDataSource(nameof(TimeOnlyCases))]
    public async Task TimeOnlyIsItsRoundTripText(string name, TimeOnly value) =>
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, "O")))
            .IsEqualTo($"\"{value.ToString("O", CultureInfo.InvariantCulture)}\"")
            .Because($"TimeOnly {name}");

    public static IEnumerable<(string Name, TimeSpan Value)> TimeSpanCases() =>
        [
            ("zero", TimeSpan.Zero),
            ("one tick", TimeSpan.FromTicks(1)),
            ("negative", TimeSpan.FromSeconds(-1.5)),
            ("days", new TimeSpan(1, 2, 3, 4, 500)),
            ("max", TimeSpan.MaxValue),
            ("min", TimeSpan.MinValue),
            ("sub millisecond", TimeSpan.FromTicks(1234567)),
        ];

    [Test]
    [MethodDataSource(nameof(TimeSpanCases))]
    public async Task TimeSpanIsItsConstantFormatText(string name, TimeSpan value) =>
        // TimeSpan.ToString() is the "c" (constant) pattern; "c" is what the generator asks for.
        await Assert
            .That(Write((ref JsonWriter w) => TypeIo.WriteFormattable(ref w, value, "c")))
            .IsEqualTo($"\"{value.ToString("c", CultureInfo.InvariantCulture)}\"")
            .Because($"TimeSpan {name}");

    // ── document placement: the value still has to be a well-formed member ──

    [Test]
    public async Task AFormattedScalarIsOneMemberAndGetsTheComma()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                TypeIo.WriteFormattable(ref w, Guid.Empty, "D");
                w.WritePropertyName("b"u8);
                TypeIo.WriteFormattable(ref w, new DateOnly(2026, 10, 27), "O");
                w.WriteEndObject();
            }
        );
        await Assert
            .That(json)
            .IsEqualTo("""{"a":"00000000-0000-0000-0000-000000000000","b":"2026-10-27"}""");
    }

    [Test]
    public async Task AFormattedScalarAsAnArrayElementGetsTheComma()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartArray();
                TypeIo.WriteFormattable(ref w, TimeSpan.FromSeconds(90), "c");
                TypeIo.WriteFormattable(ref w, Guid.Empty, "D");
                w.WriteEndArray();
            }
        );
        await Assert
            .That(json)
            .IsEqualTo("""["00:01:30","00000000-0000-0000-0000-000000000000"]""");
    }

    [Test]
    public async Task ATopLevelFormattedScalarIsCommitted()
    {
        // Depth 0: the writer commits immediately so the caller can read the buffer.
        var buffer = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buffer);
        TypeIo.WriteFormattable(ref w, new DateOnly(2026, 10, 27), "O");
        var reported = w.BytesWritten;
        await Assert.That(Encoding.UTF8.GetString(buffer.WrittenSpan)).IsEqualTo("\"2026-10-27\"");
        await Assert.That(reported).IsEqualTo((long)buffer.WrittenCount);
    }

    // ── the buffer bound: a value wider than the helper's stack buffer fails loudly ──

    [Test]
    public async Task AValueWiderThanTheBufferThrowsInsteadOfTruncating()
    {
        // 40 bytes is the widest text this family can produce (a GUID). Anything longer must
        // not be silently cut: TryFormat says "no" and the helper throws.
        var buffer = new ArrayBufferWriter<byte>(64);
        var w = new JsonWriter(buffer);
        var threw = false;
        try
        {
            TypeIo.WriteFormattable(ref w, new OverlongScalar(), ReadOnlySpan<char>.Empty);
        }
        catch (ArgumentException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue().Because("a 64-byte scalar must not be truncated");
        await Assert.That(buffer.WrittenCount).IsEqualTo(0);
    }

    // ── allocation: the whole point of the change ──

    [Test]
    public async Task FormattingAScalarAllocatesNothing()
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        var value = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        void Body()
        {
            buffer.Clear();
            var w = new JsonWriter(buffer);
            w.WriteStartObject();
            w.WritePropertyName("At"u8);
            TypeIo.WriteFormattable(ref w, value, "O");
            w.WritePropertyName("Id"u8);
            TypeIo.WriteFormattable(ref w, guid, "D");
            w.WriteEndObject();
        }

        // Nine batches, minimum taken: the JIT may allocate once while tiering, so a mean would
        // flake. This is the same judgement the long-payload allocation test uses.
        for (var i = 0; i < 3000; i++)
            Body();
        var batches = new long[9];
        for (var b = 0; b < batches.Length; b++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 200; i++)
                Body();
            batches[b] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        await Assert.That(batches.Min()).IsEqualTo(0L);
    }

    /// <summary>A scalar whose text is 64 bytes — wider than the helper's buffer on purpose.</summary>
    private readonly struct OverlongScalar : IUtf8SpanFormattable
    {
        public bool TryFormat(
            Span<byte> utf8Destination,
            out int bytesWritten,
            ReadOnlySpan<char> format,
            IFormatProvider? provider
        )
        {
            var text = "0123456789012345678901234567890123456789012345678901234567890123"u8;
            if (utf8Destination.Length < text.Length)
            {
                bytesWritten = 0;
                return false;
            }
            text.CopyTo(utf8Destination);
            bytesWritten = text.Length;
            return true;
        }
    }
}
