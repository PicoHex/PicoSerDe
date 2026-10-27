namespace PicoMsgPack.Tests;

// Task 4d: DateTime goes on the wire as the MessagePack timestamp extension (tag 0xFF),
// the shape MessagePack-CSharp emits, instead of the 28-character "O" string.
//
// These tests are the interoperability contract with the reference implementation
// (MessagePack 3.1.x, same process, both directions):
//   * our serializer writes  -> `MessagePackSerializer.Deserialize<DateTime>` reads
//   * `MessagePackSerializer.Serialize<DateTime>` writes -> our generated reader reads
//   * the values must be EQUAL TO THE TICK — "it did not throw" is not the assertion.
// The expected hex per value is spelled out, so the chosen tier (32 / 64 / 96) is pinned
// against the spec text and not just against whatever both writers happen to do.

public class MsgPackTimestampWireModel
{
    [MsgPackKey(0)]
    public DateTime When { get; set; }
}

public class MsgPackTimestampListModel
{
    [MsgPackKey(0)]
    public List<DateTime> Times { get; set; } = [];
}

public class MsgPackTimestampNullableModel
{
    [MsgPackKey(0)]
    public DateTime? When { get; set; }
}

/// <summary>
/// One probe value plus the exact bytes the timestamp extension must produce for it.
/// </summary>
/// <param name="Label">Name used in failure messages.</param>
/// <param name="Value">The instant.</param>
/// <param name="Tier">32, 64 or 96 — the encoding the spec forces for this value.</param>
/// <param name="Hex">Whole extension bytes, tag byte included.</param>
/// <param name="Why">One line on why this value lands in that tier.</param>
public sealed record TimestampCase(string Label, DateTime Value, int Tier, string Hex, string Why);

public class TimestampInteropTests
{
    /// <summary>
    /// The five probe values the task fixes, plus the two <see cref="DateTime"/> extremes:
    /// they are the boundaries where a tier switch or a sign error shows up.
    /// </summary>
    private static readonly TimestampCase[] Cases =
    [
        new(
            "1970-01-01T00:00:00Z",
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            32,
            "D6FF00000000",
            "seconds = 0 and no sub-second part: the 4-byte form"
        ),
        new(
            "2024-05-01T12:34:56.1234567Z",
            new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567),
            64,
            "D7FF1D6F32F0663236F0",
            "nanoseconds != 0 rules out timestamp32, seconds still fit 34 bits"
        ),
        new(
            "2200-01-01T00:00:00Z",
            new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            64,
            "D7FF00000001B09E1900",
            "seconds (7.26e9) exceed 2^32 but not 2^34"
        ),
        new(
            "9999-12-31T23:59:59Z",
            new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
            96,
            "C70CFF000000000000003AFFF4417F",
            "seconds (2.53e11) exceed 2^34: only the 96-bit form holds them"
        ),
        new(
            "1960-01-01T00:00:00Z",
            new DateTime(1960, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            96,
            "C70CFF00000000FFFFFFFFED300880",
            "negative seconds (before the epoch) cannot use the unsigned 32/64 forms"
        ),
        new(
            "DateTime.MinValue",
            DateTime.MinValue,
            96,
            "C70CFF00000000FFFFFFF1886E0900",
            "the smallest instant: 62 billion negative seconds, 0 nanoseconds"
        ),
        new(
            "DateTime.MaxValue",
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc),
            96,
            "C70CFF3B9AC99C0000003AFFF4417F",
            "999999900 nanoseconds + the largest representable second count"
        ),
    ];

    private const byte TimestampTag = 0xFF;

    private static byte[] Map(object key, byte[] value)
    {
        var payload = new byte[2 + value.Length];
        payload[0] = 0x81; // fixmap, 1 entry
        payload[1] = key switch
        {
            int i when i >= 0 && i <= 127 => (byte)i,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        value.CopyTo(payload, 2);
        return payload;
    }

    /// <summary>One-entry int-keyed map whose value is a MessagePack string.</summary>
    private static byte[] LegacyStringPayload(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        var header = utf8.Length <= 31 ? 1 : 2;
        var payload = new byte[2 + header + utf8.Length];
        payload[0] = 0x81; // fixmap, 1 entry
        payload[1] = 0x00; // key 0
        if (header == 1)
        {
            payload[2] = (byte)(0xA0 | utf8.Length); // fixstr
        }
        else
        {
            payload[2] = 0xD9; // str8
            payload[3] = (byte)utf8.Length;
        }
        utf8.CopyTo(payload, 2 + header);
        return payload;
    }

    private static byte[] SerializeModel(DateTime when)
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new MsgPackTimestampWireModel { When = when }
        );
        // fixmap(1) + fixint key: the two bytes in front of the value.
        return bytes.AsSpan(2).ToArray();
    }

    // ── direction 1: we write, MessagePack-CSharp reads ────────────────────────────

    [Test]
    public async Task Our_timestamp_bytes_are_read_by_MessagePackCSharp_tick_for_tick()
    {
        foreach (var c in Cases)
        {
            var ours = SerializeModel(c.Value);
            var hex = Convert.ToHexString(ours);

            await Assert.That(hex).IsEqualTo(c.Hex).Because($"{c.Label}: {c.Why}");
            // fixext4/fixext8 put the tag right after the code byte; ext8 has a length
            // byte in between.
            var tagIndex = ours[0] == 0xC7 ? 2 : 1;
            await Assert.That(ours[tagIndex]).IsEqualTo(TimestampTag).Because($"{c.Label}: tag");

            var mpc = MessagePackSerializer.Deserialize<DateTime>(ours);
            await Assert
                .That(mpc.Ticks)
                .IsEqualTo(c.Value.Ticks)
                .Because($"{c.Label}: MessagePack-CSharp must read back the same tick");
            await Assert.That(mpc.Kind).IsEqualTo(DateTimeKind.Utc).Because($"{c.Label}: kind");
        }
    }

    [Test]
    public async Task Our_full_payload_is_read_by_MessagePackCSharp_as_a_DateTime_value()
    {
        foreach (var c in Cases)
        {
            var ours = MsgPackSerializer.SerializeToUtf8Bytes(
                new MsgPackTimestampWireModel { When = c.Value }
            );

            // Untyped read: the reference implementation has to recognise tag 0xFF on the
            // wire as a DateTime, not merely skip the token.
            var dict = MessagePackSerializer.Deserialize<Dictionary<int, object>>(ours);
            await Assert.That(dict.Count).IsEqualTo(1).Because($"{c.Label}: map size");
            await Assert
                .That(dict[0])
                .IsTypeOf<DateTime>()
                .Because($"{c.Label}: MessagePack-CSharp must decode tag 0xFF into a DateTime");
            await Assert
                .That(((DateTime)dict[0]!).Ticks)
                .IsEqualTo(c.Value.Ticks)
                .Because($"{c.Label}: same instant through the untyped reader");
        }
    }

    [Test]
    public async Task Our_list_payload_is_read_by_MessagePackCSharp_tick_for_tick()
    {
        var model = new MsgPackTimestampListModel { Times = [.. Cases.Select(c => c.Value)] };

        var ours = MsgPackSerializer.SerializeToUtf8Bytes(model);
        // fixmap(1) + key int(0) in front of the array the reference implementation reads.
        var mpc = MessagePackSerializer.Deserialize<List<DateTime>>(ours.AsSpan(2).ToArray());

        await Assert.That(mpc.Count).IsEqualTo(Cases.Length).Because("element count");
        for (var i = 0; i < Cases.Length; i++)
        {
            await Assert
                .That(mpc[i].Ticks)
                .IsEqualTo(Cases[i].Value.Ticks)
                .Because($"{Cases[i].Label}: List<DateTime> element {i}");
        }
    }

    // ── direction 2: MessagePack-CSharp writes, we read ───────────────────────────

    [Test]
    public async Task MessagePackCSharp_timestamp_bytes_are_read_by_us_tick_for_tick()
    {
        foreach (var c in Cases)
        {
            var mpcBytes = MessagePackSerializer.Serialize(c.Value);
            var hex = Convert.ToHexString(mpcBytes);

            // Same tier and same bytes as ours: the wire choice is the spec's, not a
            // preference of either library.
            await Assert
                .That(hex)
                .IsEqualTo(c.Hex)
                .Because($"{c.Label}: MessagePack-CSharp must pick the same tier");
            await Assert
                .That(hex)
                .IsEqualTo(Convert.ToHexString(SerializeModel(c.Value)))
                .Because($"{c.Label}: both writers must agree byte for byte");

            var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(Map(0, mpcBytes));
            await Assert.That(back).IsNotNull();
            await Assert
                .That(back!.When.Ticks)
                .IsEqualTo(c.Value.Ticks)
                .Because($"{c.Label}: our reader must return the same tick");
            await Assert
                .That(back.When.Kind)
                .IsEqualTo(DateTimeKind.Utc)
                .Because($"{c.Label}: kind");
        }
    }

    // ── the tier choice is the spec's, checked against the spec arithmetic ────────

    [Test]
    public async Task Encoded_bytes_match_the_spec_arithmetic()
    {
        foreach (var c in Cases)
        {
            var bytes = SerializeModel(c.Value);
            var (sec, nsec, tier) = DecodePerSpec(bytes);

            await Assert.That(tier).IsEqualTo(c.Tier).Because($"{c.Label}: tier");
            await Assert.That(bytes.Length).IsEqualTo(c.Hex.Length / 2).Because($"{c.Label}: size");

            // (seconds since the epoch) * 1e9 + nanoseconds must meet the ticks at the
            // 100 ns boundary — this is where a truncating (rather than flooring)
            // division would show up for the pre-1970 values.
            var epochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            var sinceEpoch = c.Value.Ticks - epochTicks;
            var expectedSec = TsMath.DivRemFloor(sinceEpoch, TimeSpan.TicksPerSecond);
            await Assert
                .That(sec)
                .IsEqualTo(expectedSec)
                .Because($"{c.Label}: seconds since the epoch, floored");
            await Assert
                .That(nsec)
                .IsEqualTo((sinceEpoch - expectedSec * TimeSpan.TicksPerSecond) * 100)
                .Because($"{c.Label}: nanoseconds within the second");
            await Assert.That(nsec).IsLessThan(1_000_000_000L).Because($"{c.Label}: 30-bit field");
        }
    }

    private static void ThrowIfNot(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Decodes an extension the way the spec describes it, without going through either
    /// implementation's reader.
    /// </summary>
    private static (long Seconds, long Nanoseconds, int Tier) DecodePerSpec(byte[] bytes)
    {
        switch (bytes[0])
        {
            case 0xD6: // fixext4
                ThrowIfNot(bytes[1] == TimestampTag, "fixext4 without the timestamp tag");
                return (
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(2)),
                    0,
                    32
                );
            case 0xD7: // fixext8
            {
                ThrowIfNot(bytes[1] == TimestampTag, "fixext8 without the timestamp tag");
                var packed = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
                    bytes.AsSpan(2)
                );
                return ((long)(packed & 0x3FFFFFFFFUL), (long)(packed >> 34), 64);
            }
            case 0xC7: // ext8: 0xC7, length, tag
                ThrowIfNot(bytes[1] == 12, "ext8 with a length other than 12");
                ThrowIfNot(bytes[2] == TimestampTag, "ext8 without the timestamp tag");
                return (
                    System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(7)),
                    System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(3)),
                    96
                );
            default:
                throw new InvalidOperationException($"not a timestamp extension: {bytes[0]:X2}");
        }
    }

    // ── the legacy payloads must stay readable ───────────────────────────────────

    /// <summary>
    /// Payloads written by earlier PicoMsgPack builds carry the value as a 28-character
    /// ISO string. The reader keeps that branch, so an upgrade never makes stored bytes
    /// unreadable.
    /// </summary>
    [Test]
    public async Task Legacy_iso_string_payload_still_deserializes()
    {
        // 28 characters: the fixstr form earlier builds wrote.
        var payload = LegacyStringPayload("2024-05-01T12:34:56.1234567Z");

        var expected = new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567);

        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(payload);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.When.Ticks).IsEqualTo(expected.Ticks);
        await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Legacy_iso_string_payload_with_an_offset_still_deserializes()
    {
        // A Local value written by an earlier build: 33 characters, so str8, not fixstr.
        var payload = LegacyStringPayload("2024-05-01T20:34:56.1234567+08:00");

        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(payload);

        await Assert.That(back).IsNotNull();
        // The offset form denotes the same instant as 12:34:56.1234567Z.
        await Assert
            .That(back!.When.ToUniversalTime())
            .IsEqualTo(new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567));
    }

    // ── MsgPackOptions.DateTimeFormat ────────────────────────────────────────────

    [Test]
    public async Task Default_DateTimeFormat_is_the_timestamp_extension()
    {
        await Assert
            .That(new MsgPackOptions().DateTimeFormat)
            .IsEqualTo(MsgPackDateTimeFormat.TimestampExt);
        await Assert.That((int)MsgPackDateTimeFormat.TimestampExt).IsEqualTo(0);
    }

    [Test]
    public async Task IsoString_option_reproduces_the_legacy_wire()
    {
        var when = new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567);
        var options = new MsgPackOptions { DateTimeFormat = MsgPackDateTimeFormat.IsoString };

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new MsgPackTimestampWireModel { When = when },
            options
        );

        var legacyText = Encoding.UTF8.GetBytes(when.ToString("O"));
        var expected = new byte[3 + legacyText.Length];
        expected[0] = 0x81;
        expected[1] = 0x00;
        expected[2] = (byte)(0xA0 | legacyText.Length);
        legacyText.CopyTo(expected, 3);

        await Assert
            .That(Convert.ToHexString(bytes))
            .IsEqualTo(Convert.ToHexString(expected))
            .Because("IsoString must stay byte-compatible with the pre-4d payloads");

        // …and the reader still accepts it.
        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(bytes, options);
        await Assert.That(back!.When.Ticks).IsEqualTo(when.Ticks);
    }

    // ── Kind policy ──────────────────────────────────────────────────────────────

    [Test]
    public async Task Utc_values_keep_their_ticks()
    {
        var when = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Utc).AddTicks(4567);

        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(new MsgPackTimestampWireModel { When = when })
        );

        await Assert.That(back!.When.Ticks).IsEqualTo(when.Ticks);
        await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Unspecified_values_are_interpreted_as_Utc()
    {
        var when = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Unspecified).AddTicks(4567);

        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(new MsgPackTimestampWireModel { When = when })
        );

        // Ticks go on the wire unchanged (the value is taken at face value, as UTC)…
        await Assert.That(back!.When.Ticks).IsEqualTo(when.Ticks);
        // …and the kind is normalised to Utc, because the wire carries an instant only.
        await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Local_values_are_converted_to_Utc()
    {
        var when = new DateTime(2026, 7, 8, 9, 10, 11, DateTimeKind.Local).AddTicks(4567);
        var utc = when.ToUniversalTime();

        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(
            new MsgPackTimestampWireModel { When = when }
        );

        // The bytes are exactly the bytes of the UTC instant…
        await Assert
            .That(Convert.ToHexString(bytes.AsSpan(2)))
            .IsEqualTo(Convert.ToHexString(SerializeModel(utc)))
            .Because("a Local value is converted with ToUniversalTime()");

        // …so the instant survives and the kind comes back as Utc.
        var back = MsgPackSerializer.Deserialize<MsgPackTimestampWireModel>(bytes);
        await Assert.That(back!.When.Ticks).IsEqualTo(utc.Ticks);
        await Assert.That(back.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    // ── nullable / nested shapes follow the same rule ────────────────────────────

    [Test]
    public async Task Nullable_datetime_writes_the_extension_and_reads_back()
    {
        var when = new DateTime(2024, 5, 1, 12, 34, 56, DateTimeKind.Utc).AddTicks(1_234_567);

        var payload = MsgPackSerializer.SerializeToUtf8Bytes(
            new MsgPackTimestampNullableModel { When = when }
        );
        await Assert
            .That(Convert.ToHexString(payload.AsSpan(2)))
            .IsEqualTo("D7FF1D6F32F0663236F0")
            .Because("a DateTime? member must use the same encoding as DateTime");

        var back = MsgPackSerializer.Deserialize<MsgPackTimestampNullableModel>(payload);
        await Assert.That(back!.When!.Value.Ticks).IsEqualTo(when.Ticks);

        var nullPayload = MsgPackSerializer.SerializeToUtf8Bytes(
            new MsgPackTimestampNullableModel { When = null }
        );
        var nullBack = MsgPackSerializer.Deserialize<MsgPackTimestampNullableModel>(nullPayload);
        await Assert.That(nullBack!.When).IsNull();
    }
}

file static class TsMath
{
    /// <summary>Floor division, so pre-1970 instants keep a non-negative remainder.</summary>
    internal static long DivRemFloor(long value, long divisor)
    {
        var quotient = value / divisor;
        if (value % divisor < 0)
            quotient--;
        return quotient;
    }
}
