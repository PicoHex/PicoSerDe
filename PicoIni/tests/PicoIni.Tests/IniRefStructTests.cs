using System.Buffers;
using System.Text;
using PicoSerDe.Core;

namespace PicoIni.Tests;

public ref struct IniPoint
{
    public int X,
        Y;
}

public class IniSimpleModel
{
    public string Title { get; set; } = "";
    public int Port { get; set; }
}

public class IniRefStructTests
{
    [Test]
    public async Task SourceGen_Generates_Serializer_For_RefStruct()
    {
        var v = new IniPoint { X = 10, Y = 20 };
        var ini = IniSerializer.Serialize(v);

        await Assert.That(ini).Contains("10");
        await Assert.That(ini).Contains("20");
        await Assert.That(ini).Contains("X");
        await Assert.That(ini).Contains("Y");
    }

    [Test]
    public async Task RegularType_Still_Works()
    {
        var cfg = new IniSimpleModel { Title = "Test", Port = 8080 };
        var ini = IniSerializer.Serialize(cfg);
        await Assert.That(ini).Contains("Title");
        await Assert.That(ini).Contains("8080");
    }
}
