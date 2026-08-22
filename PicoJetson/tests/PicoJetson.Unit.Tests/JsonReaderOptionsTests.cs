namespace PicoJetson.Tests;

public class JsonReaderOptionsTests
{
    [Test]
    public async Task Reader_UsesCtorOptions_ForCommentHandling()
    {
        var data = "{ /* c */ \"a\": 1 }"u8;
        var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        var reader = new JsonReader(data, options: opts);
        await Assert.That(reader.Read()).IsTrue();
    }

    [Test]
    public async Task Reader_DefaultOptions_DisallowComments()
    {
        var data = "{ /* c */ \"a\": 1 }"u8;
        var reader = new JsonReader(data);
        var threw = false;
        try
        {
            while (reader.Read()) { }
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task Reader_ExposesOptions()
    {
        var opts = new JsonOptions { ReadCommentHandling = JsonCommentHandling.Skip };
        var reader = new JsonReader("{}"u8, options: opts);
        await Assert.That(reader.Options).IsEqualTo(opts);
    }

    [Test]
    public async Task Reader_OptionsNull_ByDefault()
    {
        var reader = new JsonReader("{}"u8);
        await Assert.That(reader.Options).IsNull();
    }
}

public class JsonWriterOptionsTests
{
    [Test]
    public async Task Writer_UsesCtorOptions_ForNumberHandling()
    {
        var buf = new ArrayBufferWriter<byte>();
        var opts = new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var writer = new JsonWriter(buf, options: opts);
        writer.WriteNumber(double.NaN);
        await Assert.That(buf.WrittenSpan.SequenceEqual("NaN"u8)).IsTrue();
    }

    [Test]
    public async Task Writer_ExposesOptions()
    {
        var opts = new JsonOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
        var writer = new JsonWriter(new ArrayBufferWriter<byte>(), options: opts);
        await Assert.That(writer.Options).IsEqualTo(opts);
    }
}
