namespace PicoIni.Tests;

public class IniReaderTests
{
    [Test]
    public async Task Read_Section_ReturnsSectionStart()
    {
        var data = "[Server]"u8;
        bool ok;
        bool sectionOk;
        {
            var r = new IniReader(data);
            ok = r.Read();
            sectionOk = r.SectionName.SequenceEqual("Server"u8);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(sectionOk).IsTrue();
    }

    [Test]
    public async Task Read_KeyValue_ReturnsKey()
    {
        var data = "Host = localhost"u8;
        bool ok;
        bool keyOk;
        bool valOk;
        {
            var reader = new IniReader(data);
            ok = reader.Read();
            keyOk = reader.Key.SequenceEqual("Host"u8);
            valOk = reader.ValueSpan.SequenceEqual("localhost"u8);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(keyOk).IsTrue();
        await Assert.That(valOk).IsTrue();
    }

    [Test]
    public async Task Read_SemicolonComment_ReturnsComment()
    {
        var data = "; this is a comment"u8;
        IniTokenType tt;
        bool ctOk;
        {
            var r = new IniReader(data);
            r.Read();
            tt = r.TokenType;
            ctOk = r.CommentText.SequenceEqual(" this is a comment"u8);
        }
        await Assert.That(tt).IsEqualTo(IniTokenType.Comment);
        await Assert.That(ctOk).IsTrue();
    }

    [Test]
    public async Task Read_HashComment_ReturnsComment()
    {
        var data = "# config file"u8;
        IniTokenType tt;
        {
            var r = new IniReader(data);
            r.Read();
            tt = r.TokenType;
        }
        await Assert.That(tt).IsEqualTo(IniTokenType.Comment);
    }

    [Test]
    public async Task Read_BlankLine_ReturnsBlank()
    {
        var data = "\r\n[Section]"u8;
        IniTokenType tt;
        {
            var r = new IniReader(data);
            r.Read();
            tt = r.TokenType;
        }
        await Assert.That(tt).IsEqualTo(IniTokenType.Blank);
    }

    [Test]
    public async Task Read_EmptyInput_ReturnsFalse()
    {
        bool ok;
        {
            var r = new IniReader(default(ReadOnlySpan<byte>));
            ok = r.Read();
        }
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task Read_QuotedValue_Unquotes()
    {
        var data = "Name = \"hello world\""u8;
        bool valOk;
        {
            var r = new IniReader(data);
            r.Read();
            valOk = r.ValueSpan.SequenceEqual("hello world"u8);
        }
        await Assert.That(valOk).IsTrue();
    }

    [Test]
    public async Task TryGetInt32_ParsesInteger()
    {
        var data = "Count = 42"u8;
        bool ok;
        int v;
        {
            var r = new IniReader(data);
            r.Read();
            ok = r.TryGetInt32(out v);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(v).IsEqualTo(42);
    }

    [Test]
    public async Task TryGetBool_Works()
    {
        var d1 = "Enabled = true"u8;
        var d2 = "Enabled = false"u8;
        bool ok1,
            ok2,
            v1,
            v2;
        {
            var r = new IniReader(d1);
            r.Read();
            ok1 = r.TryGetBool(out v1);
        }
        {
            var r = new IniReader(d2);
            r.Read();
            ok2 = r.TryGetBool(out v2);
        }
        await Assert.That(ok1).IsTrue();
        await Assert.That(v1).IsTrue();
        await Assert.That(ok2).IsTrue();
        await Assert.That(v2).IsFalse();
    }

    [Test]
    public async Task SectionNameEquals_CaseInsensitive()
    {
        var data = "[Server]"u8;
        bool eq1,
            eq2,
            eq3;
        {
            var r = new IniReader(data);
            r.Read();
            eq1 = r.SectionNameEquals("SERVER"u8);
            eq2 = r.SectionNameEquals("server"u8);
            eq3 = r.SectionNameEquals("Server"u8);
        }
        await Assert.That(eq1).IsTrue();
        await Assert.That(eq2).IsTrue();
        await Assert.That(eq3).IsTrue();
    }

    [Test]
    public async Task MultiValue_Section()
    {
        var data = "; Config\r\n[Server]\r\nHost = localhost\r\nPort = 8080"u8;
        bool cOk = false,
            sOk = false,
            hOk = false,
            pOk = false;
        int step = 0;
        {
            var r = new IniReader(data);
            while (r.Read())
            {
                if (step == 0)
                {
                    cOk = r.TokenType == IniTokenType.Comment;
                }
                else if (step == 1)
                {
                    sOk =
                        r.TokenType == IniTokenType.SectionStart
                        && r.SectionName.SequenceEqual("Server"u8);
                }
                else if (step == 2)
                {
                    hOk = r.Key.SequenceEqual("Host"u8) && r.ValueSpan.SequenceEqual("localhost"u8);
                }
                else if (step == 3)
                {
                    pOk = r.Key.SequenceEqual("Port"u8) && r.ValueSpan.SequenceEqual("8080"u8);
                }
                step++;
            }
        }
        await Assert.That(cOk).IsTrue();
        await Assert.That(sOk).IsTrue();
        await Assert.That(hOk).IsTrue();
        await Assert.That(pOk).IsTrue();
    }

    [Test]
    public async Task SequenceReader_Parses()
    {
        var data = "[Test]\nKey = Val"u8.ToArray();
        var seq = new ReadOnlySequence<byte>(data);
        bool ok;
        bool sOk;
        {
            var r = new IniReader(seq);
            ok = r.Read();
            sOk = r.SectionName.SequenceEqual("Test"u8);
        }
        await Assert.That(ok).IsTrue();
        await Assert.That(sOk).IsTrue();
    }

    [Test]
    public async Task MalformedSection_ThrowsFormatException()
    {
        var data = "[unclosed"u8;
        bool threw = false;
        try
        {
            var r = new IniReader(data);
            r.Read();
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
    }
}
