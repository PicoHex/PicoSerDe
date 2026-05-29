namespace PicoIni.Tests;

public class IniReaderUnifiedTests
{
    [Test]
    public async Task Read_Section_ReturnsObjectStart()
    {
        var data = "[Server]"u8.ToArray();
        bool readOk;
        TokenType tt;
        string sectionName;
        using (var reader = new IniReader(data))
        {
            readOk = reader.Read();
            tt = reader.TokenType;
            sectionName = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(readOk).IsTrue();
        await Assert.That(tt).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(sectionName).IsEqualTo("Server");
    }

    [Test]
    public async Task Read_TwoSections_EmitsImplicitEndBeforeSecondStart()
    {
        var data = "[A]\n[B]"u8.ToArray();
        TokenType t1, t2, t3;
        string s3;
        using (var reader = new IniReader(data))
        {
            reader.Read(); t1 = reader.TokenType;
            reader.Read(); t2 = reader.TokenType;
            reader.Read(); t3 = reader.TokenType;
            s3 = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(t1).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(t2).IsEqualTo(TokenType.ObjectEnd);
        await Assert.That(t3).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(s3).IsEqualTo("B");
    }

    [Test]
    public async Task Read_KeyValue_ReturnsPropertyNameThenString()
    {
        var data = "Host = localhost"u8.ToArray();
        TokenType tt1;
        string v1, v2;
        using (var reader = new IniReader(data))
        {
            reader.Read(); tt1 = reader.TokenType; v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read(); v2 = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(v1).IsEqualTo("Host");
        await Assert.That(v2).IsEqualTo("localhost");
    }

    [Test]
    public async Task TryGetInt32_ParsesInteger()
    {
        var data = "Port = 8080"u8.ToArray();
        int v; bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(8080);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var data = "Enabled = true"u8.ToArray();
        bool v, ok;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read();
            ok = reader.TryGetBool(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task TryGetFloat64_ParsesFloat()
    {
        var data = "Rate = 3.14"u8.ToArray();
        double v; bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read();
            ok = reader.TryGetFloat64(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task TryGetInt32_ParsesNegative()
    {
        var data = "Offset = -42"u8.ToArray();
        int v; bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(-42);
    }

    [Test]
    public async Task Read_Comment_Skipped_ThenNextToken()
    {
        var data = "; comment\n[Server]"u8.ToArray();
        TokenType tt;
        using (var reader = new IniReader(data)) { reader.Read(); tt = reader.TokenType; }
        await Assert.That(tt).IsEqualTo(TokenType.ObjectStart);
    }

    [Test]
    public async Task Read_BlankLines_Skipped_ThenNextToken()
    {
        var data = "\n\n[Server]"u8.ToArray();
        TokenType tt;
        using (var reader = new IniReader(data)) { reader.Read(); tt = reader.TokenType; }
        await Assert.That(tt).IsEqualTo(TokenType.ObjectStart);
    }

    [Test]
    public async Task Read_QuotedValue_Unquotes()
    {
        var data = "Name = \"hello world\""u8.ToArray();
        string v;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read();
            v = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(v).IsEqualTo("hello world");
    }

    [Test]
    public async Task Read_EndOfInput_ReturnsFalse()
    {
        var data = "Key = Value"u8.ToArray();
        bool last;
        using (var reader = new IniReader(data))
        {
            reader.Read(); reader.Read(); last = reader.Read();
        }
        await Assert.That(last).IsFalse();
    }
}
