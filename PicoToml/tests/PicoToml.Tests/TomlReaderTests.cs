namespace PicoToml.Tests;

public class TomlReaderTests
{
    [Test]
    public async Task BareKey_StringValue()
    {
        var r = new TomlReader("name = \"Alice\"\n"u8);
        var ok = r.Read(); var tt = r.TokenType;
        var key = Encoding.UTF8.GetString(r.KeySpan); var val = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(ok).IsTrue(); await Assert.That(tt).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("name"); await Assert.That(val).IsEqualTo("Alice");
    }
    [Test]
    public async Task IntegerValue_TryGetInt32()
    {
        var r = new TomlReader("port = 8080\n"u8); r.Read();
        var ok = r.TryGetInt32(out var p);
        await Assert.That(ok).IsTrue(); await Assert.That(p).IsEqualTo(8080);
    }
    [Test]
    public async Task Read_ReturnsFalse_AtEOF()
    {
        var r = new TomlReader("x = 1\n"u8); r.Read();
        await Assert.That(r.Read()).IsFalse();
    }
    [Test]
    public async Task BooleanValue_TryGetBool()
    {
        var r = new TomlReader("enabled = true\n"u8); r.Read();
        var ok = r.TryGetBool(out var v); await Assert.That(ok).IsTrue(); await Assert.That(v).IsTrue();
        var r2 = new TomlReader("flag = false\n"u8); r2.Read();
        var ok2 = r2.TryGetBool(out var v2); await Assert.That(ok2).IsTrue(); await Assert.That(v2).IsFalse();
    }
    [Test]
    public async Task Comment_IsSkipped()
    {
        var r = new TomlReader("# comment\nkey = \"value\"\n"u8); r.Read();
        await Assert.That(Encoding.UTF8.GetString(r.KeySpan)).IsEqualTo("key");
    }
    [Test]
    public async Task FloatValue_TryGetFloat64()
    {
        var r = new TomlReader("pi = 3.14\n"u8); r.Read();
        var ok = r.TryGetFloat64(out var pi);
        await Assert.That(ok).IsTrue(); await Assert.That(pi).IsGreaterThan(3.13);
    }
    [Test]
    public async Task TableHeader_EmitsObjectStart()
    {
        var r = new TomlReader("[server]\nhost = \"localhost\"\n"u8);
        r.Read(); var tt1 = r.TokenType; var tbl = Encoding.UTF8.GetString(r.TablePath);
        r.Read(); var tt2 = r.TokenType; var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(tt1).IsEqualTo(TokenType.ObjectStart); await Assert.That(tbl).IsEqualTo("server");
        await Assert.That(tt2).IsEqualTo(TokenType.PropertyName); await Assert.That(key).IsEqualTo("host");
    }
    [Test]
    public async Task ArrayTable_EmitsArrayStart()
    {
        var r = new TomlReader("[[fruits]]\nname = \"apple\"\n"u8);
        r.Read(); var tt = r.TokenType; var isArr = r.IsArrayTable; var tbl = Encoding.UTF8.GetString(r.TablePath);
        r.Read(); var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(tt).IsEqualTo(TokenType.ArrayStart); await Assert.That(isArr).IsTrue();
        await Assert.That(tbl).IsEqualTo("fruits"); await Assert.That(key).IsEqualTo("name");
    }
    [Test]
    public async Task NestedTablePath()
    {
        var r = new TomlReader("[database.server]\nport = 5432\n"u8);
        r.Read(); var tbl = Encoding.UTF8.GetString(r.TablePath);
        r.Read();
        var key = Encoding.UTF8.GetString(r.KeySpan); var ok = r.TryGetInt32(out var port);
        await Assert.That(tbl).IsEqualTo("database.server"); await Assert.That(key).IsEqualTo("port");
        await Assert.That(ok).IsTrue(); await Assert.That(port).IsEqualTo(5432);
    }
    [Test]
    public async Task QuotedKey()
    {
        var r = new TomlReader("\"my-key\" = \"val\"\n"u8);
        r.Read(); var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(key).IsEqualTo("my-key");
    }
    [Test]
    public async Task TryGetInt64()
    {
        var r = new TomlReader("big = 9999999999\n"u8); r.Read();
        var ok = r.TryGetInt64(out var v);
        await Assert.That(ok).IsTrue(); await Assert.That(v).IsEqualTo(9999999999L);
    }
}
