namespace PicoMsgPack.Tests;

/// <summary>
/// Review P2-4: an empty payload or trailing bytes after the root value must
/// fail loudly instead of silently producing a default object / ignoring data.
/// </summary>
public class MsgPackStrictPayloadTests
{
    [Test]
    public async Task Deserialize_EmptyPayload_Throws()
    {
        await Assert
            .That(() => MsgPackSerializer.Deserialize<TrailingDto>(Array.Empty<byte>()))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Deserialize_TrailingByte_Throws()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new TrailingDto { Name = "x" });
        var longer = new byte[bytes.Length + 1];
        bytes.CopyTo(longer, 0);
        longer[^1] = 0xC0; // nil

        await Assert
            .That(() => MsgPackSerializer.Deserialize<TrailingDto>(longer))
            .Throws<FormatException>();
    }

    [Test]
    public async Task Deserialize_ValidPayload_StillWorks()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new TrailingDto { Name = "x" });
        var back = MsgPackSerializer.Deserialize<TrailingDto>(bytes);
        await Assert.That(back!.Name).IsEqualTo("x");
    }
}

public class TrailingDto
{
    public string Name { get; set; } = "";
}
