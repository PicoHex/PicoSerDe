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
            reader.Read();
            t1 = reader.TokenType;
            reader.Read();
            t2 = reader.TokenType;
            reader.Read();
            t3 = reader.TokenType;
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
        TokenType tt1, tt2;
        string v1, v2;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            tt1 = reader.TokenType;
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            tt2 = reader.TokenType;
            v2 = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(v1).IsEqualTo("Host");
        await Assert.That(tt2).IsEqualTo(TokenType.String);
        await Assert.That(v2).IsEqualTo("localhost");
    }

    [Test]
    public async Task Read_KeyValue_Int32_ReturnsInt32()
    {
        var data = "Port = 8080"u8.ToArray();
        TokenType tt;
        int v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetInt32(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(v).IsEqualTo(8080);
    }

    [Test]
    public async Task Read_KeyValue_Bool_ReturnsBool()
    {
        var data = "Enabled = true"u8.ToArray();
        TokenType tt;
        bool v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetBool(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Bool);
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task Read_KeyValue_Float_ReturnsFloat64()
    {
        var data = "Rate = 3.14"u8.ToArray();
        TokenType tt;
        double v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetFloat64(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Float64);
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task Read_Comment_Skipped_ThenNextToken()
    {
        var data = "; comment\n[Server]"u8.ToArray();
        TokenType tt;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
        }
        await Assert.That(tt).IsEqualTo(TokenType.ObjectStart);
    }

    [Test]
    public async Task Read_BlankLines_Skipped_ThenNextToken()
    {
        var data = "\n\n[Server]"u8.ToArray();
        TokenType tt;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            tt = reader.TokenType;
        }
        await Assert.That(tt).IsEqualTo(TokenType.ObjectStart);
    }

    [Test]
    public async Task Read_QuotedValue_Unquotes()
    {
        var data = "Name = \"hello world\""u8.ToArray();
        TokenType tt;
        string v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            tt = reader.TokenType;
            v = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(tt).IsEqualTo(TokenType.String);
        await Assert.That(v).IsEqualTo("hello world");
    }

    [Test]
    public async Task Read_EndOfInput_ReturnsFalse()
    {
        var data = "Key = Value"u8.ToArray();
        bool last;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            last = reader.Read();
        }
        await Assert.That(last).IsFalse();
    }

    [Test]
    public async Task Read_NegativeInt_ReturnsInt32()
    {
        var data = "Offset = -42"u8.ToArray();
        TokenType tt;
        int v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            tt = reader.TokenType;
            reader.TryGetInt32(out v);
        }
        await Assert.That(tt).IsEqualTo(TokenType.Int32);
        await Assert.That(v).IsEqualTo(-42);
    }
}
