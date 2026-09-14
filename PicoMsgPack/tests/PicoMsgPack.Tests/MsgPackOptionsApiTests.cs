namespace PicoMsgPack.Tests;

/// <summary>
/// Review P2-2: MsgPack options must also be reachable from Deserialize.
/// </summary>
public class MsgPackOptionsApiTests
{
    [Test]
    public async Task Deserialize_AcceptsOptions()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new OptionsProbeDto { Name = "x" });
        var dto = MsgPackSerializer.Deserialize<OptionsProbeDto>(bytes, new MsgPackOptions());
        await Assert.That(dto!.Name).IsEqualTo("x");
    }
}

public class OptionsProbeDto
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
}
