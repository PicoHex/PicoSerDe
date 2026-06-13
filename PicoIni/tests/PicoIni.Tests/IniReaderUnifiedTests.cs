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
        TokenType t1,
            t2,
            t3;
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
        TokenType tt1;
        string v1,
            v2;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            tt1 = reader.TokenType;
            v1 = Encoding.UTF8.GetString(reader.GetStringRaw());
            reader.Read();
            v2 = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(tt1).IsEqualTo(TokenType.PropertyName);
        await Assert.That(v1).IsEqualTo("Host");
        await Assert.That(v2).IsEqualTo("localhost");
    }

    [Test]
    public async Task TryGetInt32_ParsesInteger()
    {
        var data = "Port = 8080"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(8080);
    }

    [Test]
    public async Task TryGetBool_ParsesTrue()
    {
        var data = "Enabled = true"u8.ToArray();
        bool v,
            ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetBool(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsTrue();
    }

    [Test]
    public async Task TryGetFloat64_ParsesFloat()
    {
        var data = "Rate = 3.14"u8.ToArray();
        double v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
            ok = reader.TryGetFloat64(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(3.14);
    }

    [Test]
    public async Task TryGetInt32_ParsesNegative()
    {
        var data = "Offset = -42"u8.ToArray();
        int v;
        bool ok;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
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
        string v;
        using (var reader = new IniReader(data))
        {
            reader.Read();
            reader.Read();
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
            reader.Read();
            reader.Read();
            last = reader.Read();
        }
        await Assert.That(last).IsFalse();
    }

    [Test]
    public async Task Read_SequenceMode_SimpleKeyValue_Works()
    {
        var data = "Key = Val"u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        string s1 = "",
            s2 = "";
        // Use local function to contain ref struct
        ReadSequence(sequence, out s1, out s2);
        await Assert.That(s1).IsEqualTo("Key");
        await Assert.That(s2).IsEqualTo("Val");
    }

    private static void ReadSequence(ReadOnlySequence<byte> seq, out string key, out string value)
    {
        key = "";
        value = "";
        using var reader = new IniReader(seq);
        if (reader.Read())
            key = Encoding.UTF8.GetString(reader.GetStringRaw());
        if (reader.Read())
            value = Encoding.UTF8.GetString(reader.GetStringRaw());
    }

    [Test]
    public async Task Read_SequenceMode_QuotedValue_UnescapesCorrectly()
    {
        var data = "Name = \"hello\\nworld\""u8.ToArray();
        var sequence = new ReadOnlySequence<byte>(data);
        string key = "",
            value = "";
        ReadSequence(sequence, out key, out value);
        await Assert.That(key).IsEqualTo("Name");
        await Assert.That(value).IsEqualTo("hello\nworld");
    }

    [Test]
    public async Task Read_SequenceMode_LongSectionName_DoesNotOverflow()
    {
        // Section name > 64 bytes — exercises the buffer resize path
        var longName = new string('S', 200);
        var data = Encoding.UTF8.GetBytes($"[{longName}]\nKey = Value");
        var sequence = new ReadOnlySequence<byte>(data);
        string section = "";
        using (var reader = new IniReader(sequence))
        {
            reader.Read();
            section = Encoding.UTF8.GetString(reader.GetStringRaw());
        }
        await Assert.That(section).IsEqualTo(longName);
    }

    // ── Streaming / isFinalBlock tests ──

    [Test]
    public async Task Read_IsFinalBlock_EndOfData_NeedsMoreDataFalse()
    {
        bool result,
            needsMore;
        {
            var r = new IniReader("key = val"u8, isFinalBlock: true);
            r.Read();
            r.Read();
            result = r.Read();
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsFalse();
    }

    [Test]
    public async Task Read_NotFinalBlock_EndOfData_NeedsMoreDataTrue()
    {
        bool result,
            needsMore;
        {
            var r = new IniReader("key = val"u8, isFinalBlock: false);
            r.Read();
            r.Read();
            result = r.Read();
            needsMore = r.NeedsMoreData;
        }
        await Assert.That(result).IsFalse();
        await Assert.That(needsMore).IsTrue();
    }

    [Test]
    public async Task ExportState_RoundTrip_RestoresDepth()
    {
        int finalDepth;
        {
            var part1 = "[sec]\nk1 = v1"u8.ToArray();
            var r1 = new IniReader(new ReadOnlySequence<byte>(part1), isFinalBlock: false);
            r1.Read();
            r1.Read();
            r1.Read();
            r1.Read(); // [sec], k1, =, v1
            var state = r1.ExportState();

            var part2 = "\nk2 = v2"u8.ToArray();
            var r2 = new IniReader(new ReadOnlySequence<byte>(part2), isFinalBlock: true, state);
            r2.Read();
            r2.Read();
            r2.Read(); // k2, =, v2
            // IniReader doesn't emit ObjectEnd on EOF; depth stays 1
            finalDepth = r2.Depth;
        }
        await Assert.That(finalDepth).IsEqualTo(1); // still inside section
    }
}
