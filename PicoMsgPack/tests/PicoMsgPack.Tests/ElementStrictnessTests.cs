namespace PicoMsgPack.Tests;

public class StringHolder
{
    public string Name { get; set; } = "";
}

public class IntListHolder
{
    public List<int> Nums { get; set; } = new();
}

public class TemporalLists
{
    public List<Guid> Ids { get; set; } = new();
    public List<DateTime> Ats { get; set; } = new();
}

public class BytesListHolder
{
    public List<byte[]> Blobs { get; set; } = new();
}

public class ElementKindLists
{
    public List<TimeSpan> Spans { get; set; } = new();
    public List<DateOnly> Days { get; set; } = new();
    public List<TimeOnly> Times { get; set; } = new();
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public List<decimal> Amounts { get; set; } = new();
    public List<byte[]> Blobs { get; set; } = new();
}

public class NullableKindLists
{
    public List<Guid?> Ids { get; set; } = new();
    public List<DayOfWeek?> DaysOfWeek { get; set; } = new();
    public List<decimal?> Amounts { get; set; } = new();
}

public class ElementStrictnessTests
{
    [Test]
    public void StringMember_FromIntegerToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<StringHolder>(new byte[] { 0x81, 0x00, 0x2A })
        );

    [Test]
    public void ElementInt_FromStringToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<IntListHolder>(
                new byte[] { 0x81, 0x00, 0x92, 0x01, 0xA1, (byte)'x' }
            )
        );

    [Test]
    public void TemporalElement_FromIntegerToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<TemporalLists>(new byte[] { 0x81, 0x00, 0x91, 0x2A })
        );

    [Test]
    public void BytesElement_FromStringToken_Throws() =>
        Assert.Throws<FormatException>(() =>
            MsgPackSerializer.Deserialize<BytesListHolder>(
                new byte[] { 0x81, 0x00, 0x91, 0xA1, (byte)'x' }
            )
        );

    [Test]
    public async Task GuidAndDateTimeLists_RoundTrip()
    {
        var id = Guid.NewGuid();
        var at = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var back = MsgPackSerializer.Deserialize<TemporalLists>(
            MsgPackSerializer.SerializeToUtf8Bytes(new TemporalLists { Ids = { id }, Ats = { at } })
        );
        await Assert.That(back!.Ids[0]).IsEqualTo(id);
        await Assert.That(back.Ats[0]).IsEqualTo(at);
    }

    [Test]
    public async Task EverySupportedElementKind_RoundTrips()
    {
        var model = new ElementKindLists
        {
            Spans = { new TimeSpan(1, 2, 3) },
            Days = { new DateOnly(2024, 5, 6) },
            Times = { new TimeOnly(7, 8, 9) },
            DaysOfWeek = { DayOfWeek.Friday },
            Amounts = { 1.25m },
            Blobs = { new byte[] { 7, 8 } },
        };
        var back = MsgPackSerializer.Deserialize<ElementKindLists>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Spans[0]).IsEqualTo(new TimeSpan(1, 2, 3));
        await Assert.That(back.Days[0]).IsEqualTo(new DateOnly(2024, 5, 6));
        await Assert.That(back.Times[0]).IsEqualTo(new TimeOnly(7, 8, 9));
        await Assert.That(back.DaysOfWeek[0]).IsEqualTo(DayOfWeek.Friday);
        await Assert.That(back.Amounts[0]).IsEqualTo(1.25m);
        await Assert.That(back.Blobs[0]).IsEquivalentTo(new byte[] { 7, 8 });
    }

    [Test]
    public async Task NullableElements_KeepNullsAndValues()
    {
        var id = Guid.NewGuid();
        var model = new NullableKindLists
        {
            Ids = { id, null },
            DaysOfWeek = { DayOfWeek.Monday, null },
            Amounts = { 2.5m, null },
        };
        var back = MsgPackSerializer.Deserialize<NullableKindLists>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Ids[0]).IsEqualTo(id);
        await Assert.That(back.Ids[1]).IsNull();
        await Assert.That(back.DaysOfWeek[0]).IsEqualTo(DayOfWeek.Monday);
        await Assert.That(back.DaysOfWeek[1]).IsNull();
        await Assert.That(back.Amounts[0]).IsEqualTo(2.5m);
        await Assert.That(back.Amounts[1]).IsNull();
    }
}
