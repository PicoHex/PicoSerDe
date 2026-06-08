namespace PicoMsgPack.Tests;

// NOTE: PicoMsgPack uses integer-keyed map format, while MessagePack-CSharp's
// [MessagePackObject] uses positional array format by default. Cross-format
// testing validates that both implementations produce and consume spec-compliant
// msgpack: Pico→MsgPackC# via Dict<int,object> parsing (verifies all 18 keys),
// and MsgPackC#→Pico via PicoMsgPack reading the array format.

[MessagePackObject]
public class MpModel
{
    [Key(0)]
    public bool Bool { get; set; }

    [Key(1)]
    public int Int { get; set; }

    [Key(2)]
    public long Long { get; set; }

    [Key(3)]
    public double Double { get; set; }

    [Key(4)]
    public string String { get; set; } = "";

    [Key(5)]
    public DateTime DateTime { get; set; }

    [Key(6)]
    public TimeSpan TimeSpan { get; set; }

    [Key(7)]
    public Guid Guid { get; set; }

    [Key(8)]
    public DayOfWeek Enum { get; set; }

    [Key(9)]
    public int? NullableInt { get; set; }

    [Key(10)]
    public List<int> Ints { get; set; } = [];

    [Key(11)]
    public MpSub? Nested { get; set; }

    [Key(12)]
    public float Float { get; set; }

    [Key(13)]
    public decimal Decimal { get; set; }

    [Key(14)]
    public string? NullableString { get; set; }

    [Key(15)]
    public DateOnly DateOnly { get; set; }

    [Key(16)]
    public TimeOnly TimeOnly { get; set; }

    [Key(17)]
    public Dictionary<string, string> StringDict { get; set; } = [];
}

[MessagePackObject]
public class MpSub
{
    [Key(0)]
    public string Name { get; set; } = "";

    [Key(1)]
    public int Value { get; set; }
}

public class MsgPackCrossValidationTests
{
    private static MpModel Model =>
        new()
        {
            Bool = true,
            Int = 42,
            Long = 9_876_543_210L,
            Double = 2.71828,
            Float = 3.14f,
            Decimal = 123.45m,
            String = "Hello MsgPack!",
            NullableString = "not null",
            DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
            TimeSpan = new TimeSpan(10, 30, 0),
            DateOnly = new DateOnly(2026, 6, 4),
            TimeOnly = new TimeOnly(15, 45, 30),
            Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
            Enum = DayOfWeek.Wednesday,
            NullableInt = 77,
            Ints = [10, 20, 30],
            Nested = new() { Name = "sub", Value = 99 },
            StringDict = new() { ["k1"] = "v1" },
        };

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Model);
        var back = MsgPackSerializer.Deserialize<MpModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(Model);
        var back = MsgPackSerializer.Deserialize<MpModel>(bytes);
        await AssertMpEqual(Model, back!);
    }

    [Test]
    public async Task PicoSerialize_MsgPackCSharpDeserialize_AsDict()
    {
        // PicoMsgPack serializes as int-keyed map → MessagePack-CSharp reads as Dict<int,object>
        var picoBytes = MsgPackSerializer.SerializeToUtf8Bytes(Model);
        var dict = MessagePack.MessagePackSerializer.Deserialize<Dictionary<int, object>>(
            picoBytes
        );

        // All 18 keys from PicoMsgPack's int-keyed map are correctly parsed
        await Assert.That(dict.Count).IsEqualTo(18);
        // Verify by key presence — ensures PicoMsgPack produces valid msgpack map
        await Assert.That(dict.ContainsKey(0)).IsTrue();
        await Assert.That(dict.ContainsKey(4)).IsTrue();
        await Assert.That(dict.ContainsKey(8)).IsTrue();
        await Assert.That(dict.ContainsKey(17)).IsTrue();
        // Spot-check collection at key 10 (List<int>)
        var list10 = (System.Collections.IList)dict[10]!;
        await Assert.That(list10.Count).IsEqualTo(3);
        // Spot-check nested dict at key 17
        var dict17 = (System.Collections.IDictionary)dict[17]!;
        await Assert.That(dict17.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MsgPackCSharpSerialize_PicoDeserialize_ArrayFormat()
    {
        // Verify PicoMsgPack can read array-format msgpack using positional dispatch.
        // Type encoding differs for some types (MsgPackC# serializes enum as int,
        // DateTime as int64, etc.), so we verify format correctness via compatible types.
        var mpBytes = MessagePack.MessagePackSerializer.Serialize(Model);
        var back = MsgPackSerializer.Deserialize<MpModel>(mpBytes);

        await Assert.That(back).IsNotNull();
        // Bool, int, string, float: same encoding in both formats
        await Assert.That(back!.Bool).IsEqualTo(Model.Bool);
        await Assert.That(back.Int).IsEqualTo(Model.Int);
        await Assert.That(back.String).IsEqualTo(Model.String);
        await Assert.That(Math.Abs(back.Float - Model.Float) < 0.001f).IsTrue();
        await Assert.That(back.Long).IsEqualTo(Model.Long);
        await Assert.That(back.Double).IsEqualTo(Model.Double);
        await Assert.That(back.NullableInt).IsEqualTo(Model.NullableInt);
        await Assert.That(back.NullableString).IsEqualTo(Model.NullableString);
        await Assert.That(back.Ints).IsEquivalentTo(Model.Ints);
    }

    private static async Task AssertMpEqual(MpModel expected, MpModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(Math.Abs(actual.Float - expected.Float) < 0.001f).IsTrue();
        await Assert.That(actual.Decimal).IsEqualTo(expected.Decimal);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.NullableString).IsEqualTo(expected.NullableString);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert
            .That(actual.DateTime.ToUniversalTime())
            .IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.DateOnly).IsEqualTo(expected.DateOnly);
        await Assert.That(actual.TimeOnly).IsEqualTo(expected.TimeOnly);
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.Ints).IsEquivalentTo(expected.Ints);
        await Assert.That(actual.StringDict).IsEquivalentTo(expected.StringDict);
        if (expected.Nested is not null)
        {
            await Assert.That(actual.Nested).IsNotNull();
            await Assert.That(actual.Nested!.Name).IsEqualTo(expected.Nested.Name);
            await Assert.That(actual.Nested.Value).IsEqualTo(expected.Nested.Value);
        }
    }
}
