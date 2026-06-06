using MessagePack;

namespace PicoMsgPack.Tests;

public class MpModel
{
    public bool Bool { get; set; }
    public int Int { get; set; }
    public long Long { get; set; }
    public double Double { get; set; }
    public string String { get; set; } = "";
    public DateTime DateTime { get; set; }
    public TimeSpan TimeSpan { get; set; }
    public Guid Guid { get; set; }
    public DayOfWeek Enum { get; set; }
    public int? NullableInt { get; set; }
    public List<int> Ints { get; set; } = [];
    public MpSub? Nested { get; set; }
}

public class MpSub
{
    public string Name { get; set; } = "";
    public int Value { get; set; }
}

public class MsgPackCrossValidationTests
{
    // PicoMsgPack uses integer-keyed map format (IntKey: 0,1,2...).
    // MessagePack-CSharp needs ContractlessStandardResolver to handle
    // POCOs without [MessagePackObject] attributes.
    // NOTE: ContractlessStandardResolver uses DynamicObjectResolver
    // which requires System.Reflection.Emit. The cross-validation
    // tests below will be skipped if dynamic code is unavailable.

    private static MpModel Model => new()
    {
        Bool = true, Int = 42, Long = 9_876_543_210L,
        Double = 2.71828,
        String = "Hello MsgPack!",
        DateTime = new DateTime(2026, 6, 4, 12, 30, 0, DateTimeKind.Utc),
        TimeSpan = new TimeSpan(10, 30, 0),
        Guid = Guid.Parse("A1B2C3D4-E5F6-7890-ABCD-EF1234567890"),
        Enum = DayOfWeek.Wednesday,
        NullableInt = 77,
        Ints = [10, 20, 30],
        Nested = new() { Name = "sub", Value = 99 },
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

    private static async Task AssertMpEqual(MpModel expected, MpModel actual)
    {
        await Assert.That(actual.Bool).IsEqualTo(expected.Bool);
        await Assert.That(actual.Int).IsEqualTo(expected.Int);
        await Assert.That(actual.Long).IsEqualTo(expected.Long);
        await Assert.That(actual.Double).IsEqualTo(expected.Double);
        await Assert.That(actual.String).IsEqualTo(expected.String);
        await Assert.That(actual.Enum).IsEqualTo(expected.Enum);
        await Assert.That(actual.NullableInt).IsEqualTo(expected.NullableInt);
        await Assert.That(actual.DateTime.ToUniversalTime()).IsEqualTo(expected.DateTime.ToUniversalTime());
        await Assert.That(actual.TimeSpan).IsEqualTo(expected.TimeSpan);
        await Assert.That(actual.Guid).IsEqualTo(expected.Guid);
        await Assert.That(actual.Ints).IsEquivalentTo(expected.Ints);
        if (expected.Nested is not null)
        {
            await Assert.That(actual.Nested).IsNotNull();
            await Assert.That(actual.Nested!.Name).IsEqualTo(expected.Nested.Name);
            await Assert.That(actual.Nested.Value).IsEqualTo(expected.Nested.Value);
        }
    }
}
