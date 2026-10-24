namespace PicoMsgPack.Tests;

public class DecHolder
{
    public decimal? V { get; set; }
}

public class DtHolder
{
    public DateTime? V { get; set; }
}

public class GuidHolder
{
    public Guid? V { get; set; }
}

public class TsHolder
{
    public TimeSpan? V { get; set; }
}

public class DoHolder
{
    public DateOnly? V { get; set; }
}

public class ToHolder
{
    public TimeOnly? V { get; set; }
}

public class NullableEnumHolder
{
    public DayOfWeek? V { get; set; }
}

public class NullableListHolder
{
    public List<int>? V { get; set; }
}

public class NullableArrayHolder
{
    public int[]? V { get; set; }
}

public class NullableDictHolder
{
    public Dictionary<string, int>? V { get; set; }
}

public class NullableSemanticsTests
{
    [Test]
    public async Task TemporalNullable_RoundTripsNull()
    {
        var dec = MsgPackSerializer.Deserialize<DecHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DecHolder())
        )!;
        var dt = MsgPackSerializer.Deserialize<DtHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DtHolder())
        )!;
        var guid = MsgPackSerializer.Deserialize<GuidHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new GuidHolder())
        )!;
        var ts = MsgPackSerializer.Deserialize<TsHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new TsHolder())
        )!;
        var dobj = MsgPackSerializer.Deserialize<DoHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DoHolder())
        )!;
        var to = MsgPackSerializer.Deserialize<ToHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new ToHolder())
        )!;

        await Assert.That(dec.V).IsNull();
        await Assert.That(dt.V).IsNull();
        await Assert.That(guid.V).IsNull();
        await Assert.That(ts.V).IsNull();
        await Assert.That(dobj.V).IsNull();
        await Assert.That(to.V).IsNull();
    }

    [Test]
    public async Task NullableEnum_RoundTripsNullAndValue()
    {
        var nullEnum = MsgPackSerializer.Deserialize<NullableEnumHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new NullableEnumHolder())
        )!;
        var setEnum = MsgPackSerializer.Deserialize<NullableEnumHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(
                new NullableEnumHolder { V = DayOfWeek.Thursday }
            )
        )!;

        await Assert.That(nullEnum.V).IsNull();
        await Assert.That(setEnum.V).IsEqualTo(DayOfWeek.Thursday);
    }

    [Test]
    public async Task NullableCollections_StayNull()
    {
        var list = MsgPackSerializer.Deserialize<NullableListHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new NullableListHolder())
        )!;
        var array = MsgPackSerializer.Deserialize<NullableArrayHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new NullableArrayHolder())
        )!;
        var dict = MsgPackSerializer.Deserialize<NullableDictHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new NullableDictHolder())
        )!;

        await Assert.That(list.V).IsNull();
        await Assert.That(array.V).IsNull();
        await Assert.That(dict.V).IsNull();
    }

    [Test]
    public async Task NonNullValues_RoundTrip()
    {
        var dec = MsgPackSerializer.Deserialize<DecHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new DecHolder { V = 1.5m })
        )!;
        var list = MsgPackSerializer.Deserialize<NullableListHolder>(
            MsgPackSerializer.SerializeToUtf8Bytes(new NullableListHolder { V = [1, 2] })
        )!;

        await Assert.That(dec.V).IsEqualTo(1.5m);
        await Assert.That(list.V).IsEquivalentTo([1, 2]);
    }
}
