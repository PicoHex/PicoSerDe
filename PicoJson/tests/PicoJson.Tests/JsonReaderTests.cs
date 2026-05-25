namespace PicoJson.Tests;

public class JsonReaderTests
{
    [Test]
    public async Task ReadNull_ReturnsNullToken()
    {
        var r = new JsonReader("null"u8);
        var ok = r.Read();
        var tt = r.TokenType;
        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.Null);
    }

    [Test]
    public async Task ReadTrue_ReturnsBoolToken()
    {
        var r = new JsonReader("true"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadFalse_ReturnsBoolToken()
    {
        var r = new JsonReader("false"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
    }

    [Test]
    public async Task ReadInteger_ReturnsInt32Token()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadNegativeInteger_ReturnsInt32()
    {
        var r = new JsonReader("-17"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
    }

    [Test]
    public async Task ReadFloat_ReturnsFloat64Token()
    {
        var r = new JsonReader("3.14"u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.Float64);
    }

    [Test]
    public async Task ReadString_ReturnsStringToken()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var tt = r.TokenType;
        await Assert.That(tt).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task GetStringRaw_ReturnsDecodedBytes()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        var str = Encoding.UTF8.GetString(raw);
        await Assert.That(str).IsEqualTo("hello");
    }

    [Test]
    public async Task TryGetInt32_ParsesValue()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var ok = r.TryGetInt32(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetInt32_ReturnsFalse_OnString()
    {
        var r = new JsonReader("\"hello\""u8);
        r.Read();
        var ok = r.TryGetInt32(out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryGetFloat64_ParsesValue()
    {
        var r = new JsonReader("3.14"u8);
        r.Read();
        var ok = r.TryGetFloat64(out var v);
        var diff = Math.Abs(v - 3.14);
        await Assert.That(ok).IsTrue();
        await Assert.That(diff).IsLessThan(0.001);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var r = new JsonReader("true"u8);
        r.Read();
        var ok = r.TryGetBool(out var v);
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task ReadEmptyObject_ReturnsStartEnd()
    {
        var r = new JsonReader("{}"u8);
        var ok1 = r.Read();
        var tt1 = r.TokenType;
        var ok2 = r.Read();
        var tt2 = r.TokenType;
        var ok3 = r.Read();
        await Assert.That(ok1).IsTrue();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(ok2).IsTrue();
        await Assert.That(tt2).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(ok3).IsFalse();
    }

    [Test]
    public async Task ReadEmptyArray_ReturnsStartEnd()
    {
        var r = new JsonReader("[]"u8);
        r.Read();
        var tt1 = r.TokenType;
        r.Read();
        var tt2 = r.TokenType;
        await Assert.That(tt1).IsEqualTo(TokenType.ArrayStart);
        await Assert.That(tt2).IsEqualTo(TokenType.ArrayEnd);
    }

    [Test]
    public async Task ReadObjectWithProperty()
    {
        var r = new JsonReader("{\"name\":\"alice\"}"u8);
        r.Read();
        var tt1 = r.TokenType;
        r.Read();
        var tt2 = r.TokenType;
        r.Read();
        var tt3 = r.TokenType;
        r.Read();
        var tt4 = r.TokenType;
        var ok5 = r.Read();
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName);
        await Assert.That(tt3).IsEqualTo(TokenType.String);
        await Assert.That(tt4).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(ok5).IsFalse();
    }

    [Test]
    public async Task Depth_TracksNesting()
    {
        var r = new JsonReader("{\"a\":[1,2]}"u8);
        r.Read();
        var d1 = r.Depth;
        r.Read();
        var d2 = r.Depth;
        r.Read();
        var d3 = r.Depth;
        r.Read();
        var d4 = r.Depth;
        r.Read();
        var d5 = r.Depth;
        r.Read();
        var d6 = r.Depth;
        r.Read();
        var d7 = r.Depth;
        await Assert.That(d1).IsEqualTo(1);
        await Assert.That(d2).IsEqualTo(1);
        await Assert.That(d3).IsEqualTo(2);
        await Assert.That(d4).IsEqualTo(2);
        await Assert.That(d5).IsEqualTo(2);
        await Assert.That(d6).IsEqualTo(1);
        await Assert.That(d7).IsEqualTo(0);
    }

    [Test]
    public async Task BytesConsumed_MatchesInput()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var bc = r.BytesConsumed;
        await Assert.That(bc).IsEqualTo(2);
    }

    [Test]
    public async Task Skip_Object_AdvancesPastIt()
    {
        var r = new JsonReader("{\"a\":1} \"next\""u8);
        r.Read();
        r.Skip();
        var ok = r.Read();
        var tt = r.TokenType;
        await Assert.That(ok).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.String);
    }

    [Test]
    public async Task TrySkip_ReturnsFalse_OnMalformed()
    {
        var r = new JsonReader("{broken"u8);
        r.Read();
        var ok = r.TrySkip();
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task ValueSpan_ContainsRawBytes()
    {
        var r = new JsonReader("42"u8);
        r.Read();
        var str = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(str).IsEqualTo("42");
    }

    // === P0-2: String Unescaping Tests ===

    [Test]
    public async Task GetStringRaw_Unescapes_Quote()
    {
        var r = new JsonReader("\"he\\\"llo\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("he\"llo");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Backslash()
    {
        var r = new JsonReader("\"a\\\\b\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("a\\b");
    }

    [Test]
    public async Task GetStringRaw_Unescapes_Newline()
    {
        var r = new JsonReader("\"line1\\nline2\""u8);
        r.Read();
        var raw = r.GetStringRaw();
        await Assert.That(Encoding.UTF8.GetString(raw)).IsEqualTo("line1\nline2");
    }
}
