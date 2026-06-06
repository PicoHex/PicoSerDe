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
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsEqualTo(Model.Bool);
        await Assert.That(back.Int).IsEqualTo(Model.Int);
        await Assert.That(back.Long).IsEqualTo(Model.Long);
        await Assert.That(back.Double).IsEqualTo(Model.Double);
        await Assert.That(back.String).IsEqualTo(Model.String);
        await Assert.That(back.Enum).IsEqualTo(Model.Enum);
        await Assert.That(back.NullableInt).IsEqualTo(Model.NullableInt);
        await Assert.That(back.DateTime.ToUniversalTime()).IsEqualTo(Model.DateTime.ToUniversalTime());
        await Assert.That(back.TimeSpan).IsEqualTo(Model.TimeSpan);
        await Assert.That(back.Guid).IsEqualTo(Model.Guid);
        await Assert.That(back.Ints).IsEquivalentTo(Model.Ints);
        if (Model.Nested is not null)
        {
            await Assert.That(back.Nested).IsNotNull();
            await Assert.That(back.Nested!.Name).IsEqualTo(Model.Nested.Name);
            await Assert.That(back.Nested.Value).IsEqualTo(Model.Nested.Value);
        }
    }
}
