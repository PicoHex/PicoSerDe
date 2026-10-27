namespace PicoMsgPack.Tests;

// The three timestamp shapes MessagePack defines for DateTime (spec: "Timestamp extension type",
// tag 0xFF): 32-bit seconds, 64-bit packed (nsec << 34 | sec), 96-bit nanoseconds + int64 seconds.
// These tests feed HAND-WRITTEN canonical encodings — not what our own writer emits — so a
// mismatched field order can never be hidden by a matching decode bug.
public class MsgPackTimestampModel
{
    [MsgPackKey(0)]
    public DateTime When { get; set; }
}

public class TimestampExtensionTests
{
    // 0x81        fixmap with 1 entry
    // 0x00        key 0
    // 0xD6 0xFF   fixext4, tag 0xFF (timestamp32)
    // 0x65 53 F1 00   1700000000 seconds (big-endian)
    private static readonly byte[] Timestamp32Payload =
    [
        0x81,
        0x00,
        0xD6,
        0xFF,
        0x65,
        0x53,
        0xF1,
        0x00,
    ];

    // 0x81        fixmap with 1 entry
    // 0x00        key 0
    // 0xD7 0xFF   fixext8, tag 0xFF (timestamp64)
    // 0x1D 6F 34 54 65 53 F1 7B   (123456789 << 34) | 1700000123  → nsec in the HIGH 30 bits,
    //                             seconds in the LOW 34 bits, exactly as the MessagePack spec packs it
    private static readonly byte[] Timestamp64Payload =
    [
        0x81,
        0x00,
        0xD7,
        0xFF,
        0x1D,
        0x6F,
        0x34,
        0x54,
        0x65,
        0x53,
        0xF1,
        0x7B,
    ];

    // 0x81        fixmap with 1 entry
    // 0x00        key 0
    // 0xC7 0x0C 0xFF   ext8, length 12, tag 0xFF (timestamp96)
    // 0x3A DE 68 B1    nanosecond 987654321 (unsigned, big-endian, first)
    // 0x00 00 00 04 A8 17 C8 00   20000000000 seconds (signed, big-endian, last) — above 2^34,
    //                             so it cannot fit the 34-bit seconds field of timestamp64
    private static readonly byte[] Timestamp96Payload =
    [
        0x81,
        0x00,
        0xC7,
        0x0C,
        0xFF,
        0x3A,
        0xDE,
        0x68,
        0xB1,
        0x00,
        0x00,
        0x00,
        0x04,
        0xA8,
        0x17,
        0xC8,
        0x00,
    ];

    [Test]
    public async Task Deserialize_Timestamp32_CanonicalEncoding_YieldsUtcInstant()
    {
        var expected = new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc); // 1700000000s

        var result = MsgPackSerializer.Deserialize<MsgPackTimestampModel>(Timestamp32Payload);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.When).IsEqualTo(expected);
        await Assert.That(result.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Deserialize_Timestamp64_CanonicalEncoding_YieldsUtcInstant()
    {
        // 1700000123s + 123456789ns (1234567 ticks of 100ns)
        var expected = new DateTime(2023, 11, 14, 22, 15, 23, DateTimeKind.Utc).AddTicks(1_234_567);

        var result = MsgPackSerializer.Deserialize<MsgPackTimestampModel>(Timestamp64Payload);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.When).IsEqualTo(expected);
        await Assert.That(result.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }

    [Test]
    public async Task Deserialize_Timestamp96_CanonicalEncoding_YieldsUtcInstant()
    {
        // 20000000000s (year 2603, > 2^34) + 987654321ns (9876543 ticks of 100ns)
        var expected = new DateTime(2603, 10, 11, 11, 33, 20, DateTimeKind.Utc).AddTicks(9_876_543);

        var result = MsgPackSerializer.Deserialize<MsgPackTimestampModel>(Timestamp96Payload);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.When).IsEqualTo(expected);
        await Assert.That(result.When.Kind).IsEqualTo(DateTimeKind.Utc);
    }
}
