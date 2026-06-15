namespace PicoMsgPack.Tests;

public class OptionsTests
{
    [Test]
    public async Task MsgPackOptions_Defaults()
    {
        var opts = new MsgPackOptions();
        await Assert.That(opts.DefaultIgnoreCondition).IsEqualTo(MsgPackIgnoreCondition.Never);
    }
}

public class MsgPackCtorTests
{
    [Test]
    public async Task MsgPackImmutable_RoundTrip()
    {
        var dto = new MsgPackImmutableDto(42, "test");
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(dto);
        var result = MsgPackSerializer.Deserialize<MsgPackImmutableDto>(bytes);
        await Assert.That(result!.Value).IsEqualTo(42);
        await Assert.That(result.Label).IsEqualTo("test");
    }
}

public class MsgPackImmutableDto
{
    public int Value { get; }
    public string Label { get; }

    [MsgPackConstructor]
    public MsgPackImmutableDto(int value, string label) => (Value, Label) = (value, label);
}
