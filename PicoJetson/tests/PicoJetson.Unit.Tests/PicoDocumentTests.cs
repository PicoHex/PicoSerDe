namespace PicoJetson.Tests;

public class PicoDocumentTests
{
    // ── Parse / IsValid ──

    [Test]
    public async Task Parse_EmptyObject_ReturnsDocument()
    {
        var doc = PicoDocument.Parse("{}"u8.ToArray());
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(PicoValueKind.Object);
    }

    [Test]
    public async Task Parse_EmptyArray_ReturnsDocument()
    {
        var doc = PicoDocument.Parse("[]"u8.ToArray());
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(PicoValueKind.Array);
    }

    [Test]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<FormatException>(() => PicoDocument.Parse("{"u8.ToArray()));
    }

    [Test]
    public async Task IsValid_ValidJson_ReturnsTrue()
    {
        await Assert.That(PicoDocument.IsValid("{}"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[]"u8)).IsTrue();
        await Assert.That(PicoDocument.IsValid("[1,2,3]"u8)).IsTrue();
    }

    [Test]
    public async Task IsValid_InvalidJson_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid("{"u8)).IsFalse();
        await Assert.That(PicoDocument.IsValid("[1,"u8)).IsFalse();
    }

    [Test]
    public async Task IsValid_Empty_ReturnsFalse()
    {
        await Assert.That(PicoDocument.IsValid(default)).IsFalse();
        await Assert.That(PicoDocument.IsValid(""u8)).IsFalse();
    }

    // ── Value access ──

    [Test]
    public async Task GetString_ReturnsCorrectValue()
    {
        var doc = PicoDocument.Parse("\"hello\""u8.ToArray());
        await Assert.That(doc.RootElement.GetString()).IsEqualTo("hello");
    }

    [Test]
    public async Task GetInt32_ReturnsCorrectValue()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        await Assert.That(doc.RootElement.GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task GetBoolean_ReturnsCorrectValue()
    {
        var docTrue = PicoDocument.Parse("true"u8.ToArray());
        await Assert.That(docTrue.RootElement.GetBoolean()).IsTrue();
        var docFalse = PicoDocument.Parse("false"u8.ToArray());
        await Assert.That(docFalse.RootElement.GetBoolean()).IsFalse();
    }

    [Test]
    public async Task GetRawValue_ReturnsUtf8Bytes()
    {
        var doc = PicoDocument.Parse("\"hello\""u8.ToArray());
        var raw = doc.RootElement.GetRawValue();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("hello");
    }

    // ── Object access ──

    [Test]
    public async Task Indexer_ExistingKey_ReturnsValue()
    {
        var doc = PicoDocument.Parse("""{"name":"Alice","age":30}"""u8.ToArray());
        await Assert.That(doc.RootElement["name"].GetString()).IsEqualTo("Alice");
        await Assert.That(doc.RootElement["age"].GetInt32()).IsEqualTo(30);
    }

    [Test]
    public async Task TryGetProperty_Exists_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        var ok = doc.RootElement.TryGetProperty("x", out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val.GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task TryGetProperty_Missing_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        await Assert.That(doc.RootElement.TryGetProperty("y", out _)).IsFalse();
    }

    [Test]
    public void Indexer_MissingKey_Throws()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        Assert.Throws<KeyNotFoundException>(() => { var _ = doc.RootElement["y"]; });
    }

    [Test]
    public async Task NestedObject_AccessWorks()
    {
        var json = """{"user":{"name":"Bob","address":{"city":"NYC"}}}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert
            .That(doc.RootElement["user"]["address"]["city"].GetString())
            .IsEqualTo("NYC");
    }

    [Test]
    public async Task EnumerateObject_ReturnsAllProperties()
    {
        var doc = PicoDocument.Parse("""{"a":1,"b":2}"""u8.ToArray());
        var keys = new List<string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            keys.Add(prop.Name);
        }
        await Assert.That(keys).Contains("a");
        await Assert.That(keys).Contains("b");
        await Assert.That(keys).HasCount(2);
    }

    // ── Array access ──

    [Test]
    public async Task Array_Enumerate_Works()
    {
        var doc = PicoDocument.Parse("[10,20,30]"u8.ToArray());
        var values = new List<int>();
        foreach (var elem in doc.RootElement.EnumerateArray())
            values.Add(elem.GetInt32());
        await Assert.That(values).IsEquivalentTo(new[] { 10, 20, 30 });
    }

    [Test]
    public async Task Array_Indexer_Works()
    {
        var doc = PicoDocument.Parse("[\"a\",\"b\",\"c\"]"u8.ToArray());
        await Assert.That(doc.RootElement[0].GetString()).IsEqualTo("a");
        await Assert.That(doc.RootElement[1].GetString()).IsEqualTo("b");
        await Assert.That(doc.RootElement[2].GetString()).IsEqualTo("c");
    }

    [Test]
    public async Task Array_GetArrayLength_Works()
    {
        var doc = PicoDocument.Parse("[1,2,3,4,5]"u8.ToArray());
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(5);
    }

    [Test]
    public async Task EmptyArray_LengthZero()
    {
        var doc = PicoDocument.Parse("[]"u8.ToArray());
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(0);
    }

    // ── Mixed / complex ──

    [Test]
    public async Task ObjectWithArray_AccessWorks()
    {
        var doc = PicoDocument.Parse("""{"items":[1,2,3]}"""u8.ToArray());
        var items = doc.RootElement["items"];
        await Assert.That(items.GetArrayLength()).IsEqualTo(3);
        await Assert.That(items[0].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task ArrayOfObjects_AccessWorks()
    {
        var json = """[{"id":1,"name":"a"},{"id":2,"name":"b"}]"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(2);
        await Assert.That(doc.RootElement[0]["name"].GetString()).IsEqualTo("a");
        await Assert.That(doc.RootElement[1]["id"].GetInt32()).IsEqualTo(2);
    }

    // ── Null ──

    [Test]
    public async Task NullValue_ValueKindIsNull()
    {
        var doc = PicoDocument.Parse("null"u8.ToArray());
        await Assert.That(doc.RootElement.ValueKind).IsEqualTo(PicoValueKind.Null);
    }
}
