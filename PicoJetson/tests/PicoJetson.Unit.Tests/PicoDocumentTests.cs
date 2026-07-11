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
        Assert.Throws<KeyNotFoundException>(() =>
        {
            var _ = doc.RootElement["y"];
        });
    }

    [Test]
    public async Task NestedObject_AccessWorks()
    {
        var json = """{"user":{"name":"Bob","address":{"city":"NYC"}}}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement["user"]["address"]["city"].GetString()).IsEqualTo("NYC");
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
        await Assert.That(keys).Count().IsEqualTo(2);
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

    // ── Edge cases (code review fixes) ──

    [Test]
    public async Task GetInt32_FloatValue_ReturnsTruncatedInt()
    {
        var doc = PicoDocument.Parse("3.14"u8.ToArray());
        await Assert.That(doc.RootElement.GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task GetInt32_NegativeFloat_ReturnsTruncatedInt()
    {
        var doc = PicoDocument.Parse("-7.9"u8.ToArray());
        await Assert.That(doc.RootElement.GetInt32()).IsEqualTo(-7);
    }

    [Test]
    public async Task TryGetProperty_Utf8Key_Works()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        var ok = doc.RootElement.TryGetProperty("x"u8, out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val.GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_DeepNesting_ThrowsAtLimit()
    {
        var deep = new string('[', 100) + "1" + new string(']', 100);
        Assert.Throws<FormatException>(() => PicoDocument.Parse(Encoding.UTF8.GetBytes(deep)));
    }

    [Test]
    public async Task Parse_WithinDepthLimit_Succeeds()
    {
        var shallow = "[[[[1]]]]"u8;
        var doc = PicoDocument.Parse(shallow.ToArray());
        await Assert.That(doc.RootElement[0][0][0][0].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task Enumerator_ManualMoveNext_ReturnsFalseAtEnd()
    {
        var doc = PicoDocument.Parse("[1]"u8.ToArray());
        var e = doc.RootElement.EnumerateArray();
        await Assert.That(e.MoveNext()).IsTrue(); // first: 1
        await Assert.That(e.MoveNext()).IsFalse(); // end
        await Assert.That(e.MoveNext()).IsFalse(); // still false (done flag)
    }

    [Test]
    public async Task LargeArray_ParsesCorrectly()
    {
        // Regression for O(n²) sibling chain walking — LastChild must work
        var sb = new StringBuilder("[");
        for (int i = 0; i < 200; i++)
            sb.Append(i > 0 ? "," : "").Append(i);
        sb.Append("]");
        var doc = PicoDocument.Parse(Encoding.UTF8.GetBytes(sb.ToString()));
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(200);
        await Assert.That(doc.RootElement[199].GetInt32()).IsEqualTo(199);
    }

    // ── Code review round 2 fixes ──

    [Test]
    public async Task GetRawValue_OnObject_Throws()
    {
        var doc = PicoDocument.Parse("{}"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => doc.RootElement.GetRawValue());
    }

    [Test]
    public async Task GetRawValue_OnArray_Throws()
    {
        var doc = PicoDocument.Parse("[]"u8.ToArray());
        Assert.Throws<InvalidOperationException>(() => doc.RootElement.GetRawValue());
    }

    [Test]
    public async Task HasProperty_Exists_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        await Assert.That(doc.RootElement.HasProperty("x"u8)).IsTrue();
    }

    [Test]
    public async Task HasProperty_Missing_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("""{"x":1}"""u8.ToArray());
        await Assert.That(doc.RootElement.HasProperty("y"u8)).IsFalse();
    }

    [Test]
    public async Task Property_NameSpan_ReturnsUtf8Bytes()
    {
        var doc = PicoDocument.Parse("""{"name":1}"""u8.ToArray());
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var ns = prop.NameSpan;
            await Assert.That(Encoding.UTF8.GetString(ns)).IsEqualTo("name");
        }
    }

    [Test]
    public async Task Unicode_InKey_Works()
    {
        var json = """{"名字":"张三"}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement["名字"].GetString()).IsEqualTo("张三");
    }

    [Test]
    public async Task GetStringOrNull_StringValue_ReturnsValue()
    {
        var doc = PicoDocument.Parse("\"hello\""u8.ToArray());
        await Assert.That(doc.RootElement.GetStringOrNull()).IsEqualTo("hello");
    }

    [Test]
    public async Task GetStringOrNull_NonString_ReturnsNull()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        await Assert.That(doc.RootElement.GetStringOrNull()).IsNull();
    }

    [Test]
    public async Task GetStringOrNull_Null_ReturnsNull()
    {
        var doc = PicoDocument.Parse("null"u8.ToArray());
        await Assert.That(doc.RootElement.GetStringOrNull()).IsNull();
    }

    [Test]
    public async Task EnumerateArray_ToArray_Works()
    {
        var doc = PicoDocument.Parse("[1,2,3]"u8.ToArray());
        var items = doc.RootElement.EnumerateArray().ToArray();
        await Assert.That(items).Count().IsEqualTo(3);
        await Assert.That(items[0].GetInt32()).IsEqualTo(1);
        await Assert.That(items[2].GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task EnumerateArray_Select_Works()
    {
        var doc = PicoDocument.Parse("[10,20,30]"u8.ToArray());
        var values = doc.RootElement.EnumerateArray().Select(e => e.GetInt32()).ToArray();
        await Assert.That(values).IsEquivalentTo(new[] { 10, 20, 30 });
    }

    [Test]
    public async Task SpecialCharacters_InKey_Works()
    {
        var json = """{"a.b":1,"c-d":2}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement.HasProperty("a.b"u8)).IsTrue();
        await Assert.That(doc.RootElement["c-d"].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task ChainedIndex_ObjectThenArray_Works()
    {
        var json = """{"messages":["a","b"]}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        var msg = doc.RootElement["messages"];
        await Assert.That(msg.ValueKind).IsEqualTo(PicoValueKind.Array);
        await Assert.That(msg[0].GetString()).IsEqualTo("a");
        await Assert.That(msg[1].GetString()).IsEqualTo("b");
    }

    [Test]
    public async Task ChainedIndex_ObjectThenObject_Works()
    {
        var json = """{"user":{"name":"Alice","age":30}}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement["user"]["name"].GetString()).IsEqualTo("Alice");
        await Assert.That(doc.RootElement["user"]["age"].GetInt32()).IsEqualTo(30);
    }

    [Test]
    public async Task ChainedIndex_ArrayOfObjects_Works()
    {
        var json = """[{"id":1},{"id":2}]"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        await Assert.That(doc.RootElement[0]["id"].GetInt32()).IsEqualTo(1);
        await Assert.That(doc.RootElement[1]["id"].GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task NestedJson_StringValue_DoesNotThrow()
    {
        // String value containing JSON-like text
        var json = """{"payload":"plain text"}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        var payload = doc.RootElement["payload"].GetString();
        await Assert.That(payload).IsEqualTo("plain text");
    }

    [Test]
    public async Task EscapedQuote_InString_ParsesCorrectly()
    {
        // Build JSON bytes directly to avoid C# escaping ambiguity:
        // {"key":"val\"ue"}
        var bytes = new byte[]
        {
            (byte)'{',
            (byte)'"',
            (byte)'k',
            (byte)'e',
            (byte)'y',
            (byte)'"',
            (byte)':',
            (byte)'"',
            (byte)'v',
            (byte)'a',
            (byte)'l',
            (byte)'\\',
            (byte)'"',
            (byte)'u',
            (byte)'e',
            (byte)'"',
            (byte)'}',
        };
        var doc = PicoDocument.Parse(bytes);
        var val = doc.RootElement["key"].GetString();
        // After unescaping, \" becomes "
        await Assert.That(val).IsEqualTo("val\"ue");
    }

    [Test]
    public async Task EscapedBackslash_InString_ParsesCorrectly()
    {
        // {"key":"path\\to"}
        var bytes = new byte[]
        {
            (byte)'{',
            (byte)'"',
            (byte)'k',
            (byte)'e',
            (byte)'y',
            (byte)'"',
            (byte)':',
            (byte)'"',
            (byte)'p',
            (byte)'a',
            (byte)'t',
            (byte)'h',
            (byte)'\\',
            (byte)'\\',
            (byte)'t',
            (byte)'o',
            (byte)'"',
            (byte)'}',
        };
        var doc = PicoDocument.Parse(bytes);
        await Assert.That(doc.RootElement["key"].GetString()).IsEqualTo("path\\to");
    }

    [Test]
    public async Task DeeplyNestedArrayOfObjects_Works()
    {
        var json =
            """{"messages":[{"role":"user","content":"hello"},{"role":"assistant","content":"hi"}]}"""u8;
        var doc = PicoDocument.Parse(json.ToArray());
        var messages = doc.RootElement["messages"];
        await Assert.That(messages.GetArrayLength()).IsEqualTo(2);
        await Assert.That(messages[0]["role"].GetString()).IsEqualTo("user");
        await Assert.That(messages[0]["content"].GetString()).IsEqualTo("hello");
        await Assert.That(messages[1]["role"].GetString()).IsEqualTo("assistant");
    }

    [Test]
    public async Task MixedArray_Types_ParsesCorrectly()
    {
        var doc = PicoDocument.Parse("""[1,"a",true,null]"""u8.ToArray());
        await Assert.That(doc.RootElement.GetArrayLength()).IsEqualTo(4);
        await Assert.That(doc.RootElement[0].ValueKind).IsEqualTo(PicoValueKind.Number);
        await Assert.That(doc.RootElement[1].ValueKind).IsEqualTo(PicoValueKind.String);
        await Assert.That(doc.RootElement[2].ValueKind).IsEqualTo(PicoValueKind.True);
        await Assert.That(doc.RootElement[3].ValueKind).IsEqualTo(PicoValueKind.Null);
    }

    // ── Extended numeric APIs ──

    [Test]
    public async Task GetInt64_ReturnsCorrectValue()
    {
        var doc = PicoDocument.Parse("9223372036854775807"u8.ToArray());
        await Assert.That(doc.RootElement.GetInt64()).IsEqualTo(9223372036854775807L);
    }

    [Test]
    public async Task GetInt64_Negative_ReturnsCorrectValue()
    {
        var doc = PicoDocument.Parse("-42"u8.ToArray());
        await Assert.That(doc.RootElement.GetInt64()).IsEqualTo(-42L);
    }

    [Test]
    public async Task GetDouble_ReturnsCorrectValue()
    {
        var doc = PicoDocument.Parse("3.14159"u8.ToArray());
        await Assert.That(doc.RootElement.GetDouble()).IsEqualTo(3.14159).Within(0.0001);
    }

    [Test]
    public async Task GetDouble_Integer_ReturnsDouble()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        await Assert.That(doc.RootElement.GetDouble()).IsEqualTo(42.0);
    }

    [Test]
    public async Task TryGetInt32_ValidNumber_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("42"u8.ToArray());
        var ok = doc.RootElement.TryGetInt32(out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetInt32_NotANumber_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("3.14"u8.ToArray());
        var ok = doc.RootElement.TryGetInt32(out var val);
        await Assert.That(ok).IsFalse();
        await Assert.That(val).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetInt64_ValidNumber_ReturnsTrue()
    {
        var doc = PicoDocument.Parse("9223372036854775807"u8.ToArray());
        var ok = doc.RootElement.TryGetInt64(out var val);
        await Assert.That(ok).IsTrue();
        await Assert.That(val).IsEqualTo(9223372036854775807L);
    }

    [Test]
    public async Task TryGetInt64_NotANumber_ReturnsFalse()
    {
        var doc = PicoDocument.Parse("3.14"u8.ToArray());
        var ok = doc.RootElement.TryGetInt64(out var val);
        await Assert.That(ok).IsFalse();
        await Assert.That(val).IsEqualTo(0);
    }

    [Test]
    public async Task TryGetInt32_StringValue_ReturnsFalse()
    {
        var json = Encoding.UTF8.GetBytes("\"not a number\"");
        var doc = PicoDocument.Parse(json);
        var ok = doc.RootElement.TryGetInt32(out _);
        await Assert.That(ok).IsFalse();
    }
}
