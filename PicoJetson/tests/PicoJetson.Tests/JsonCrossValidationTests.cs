using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PicoCrossValidation;

namespace PicoJetson.Tests;

public class JsonCrossValidationTests
{
    private static readonly JsonSerializerOptions StjOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static ComplexModel Model => ComplexModelFactory.Create();

    [Test]
    public async Task Sg_Trigger()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes);
        await Assert.That(back).IsNotNull();
    }

    [Test]
    public async Task PicoSerialize_StjDeserialize()
    {
        var picoBytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var stj = System.Text.Json.JsonSerializer.Deserialize<ComplexModel>(picoBytes, StjOptions);
        await Assert.That(stj).IsNotNull();
    }

    [Test]
    public async Task StjSerialize_PicoDeserialize()
    {
        var stjBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Model, StjOptions);
        var pico = JsonSerializer.Deserialize<ComplexModel>(stjBytes);
        await Assert.That(pico).IsNotNull();
    }

    [Test]
    public async Task PicoRoundTrip()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Model);
        var back = JsonSerializer.Deserialize<ComplexModel>(bytes);
        await Assert.That(back).IsNotNull();
        await Assert.That(back.Bool).IsEqualTo(Model.Bool);
        await Assert.That(back.Int).IsEqualTo(Model.Int);
        await Assert.That(back.String).IsEqualTo(Model.String);
        await Assert.That(back.Enum).IsEqualTo(Model.Enum);
        await Assert.That(back.IntList).IsEquivalentTo(Model.IntList);
        await Assert.That(back.StringList).IsEquivalentTo(Model.StringList);
        await Assert.That(back.StringDict).IsEquivalentTo(Model.StringDict);
    }

    [Test]
    public async Task StjSerialize_PicoDeserialize_EscapedStrings()
    {
        var model = new ComplexModel
        {
            String = "line1\nline2\ttab\"quote\\slash",
            DateTime = DateTime.UtcNow,
            Guid = Guid.NewGuid(),
        };
        var stjBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(model, StjOptions);
        var pico = JsonSerializer.Deserialize<ComplexModel>(stjBytes);
        await Assert.That(pico).IsNotNull();
        await Assert.That(pico.String).IsEqualTo(model.String);
    }
}
