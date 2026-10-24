namespace PicoToml.Tests;

// Models at namespace level (SG skips nested types)

/// <summary>Quoted key <c>a.b</c> — must stay a single key, never a table path.</summary>
public class LiteralQuotedDotKeyDto
{
    [TomlKey("a.b")]
    public int Value { get; set; }
}

/// <summary>Quoted key <c>a b</c> — the space is part of the key.</summary>
public class LiteralQuotedSpaceKeyDto
{
    [TomlKey("a b")]
    public int Value { get; set; }
}

/// <summary>Literal-string key <c>a\.b</c> — the backslash is literal, nothing is unescaped.</summary>
public class LiteralQuotedBackslashKeyDto
{
    [TomlKey("a\\.b")]
    public int Value { get; set; }
}

/// <summary>Literal-string value that happens to spell <c>null</c>.</summary>
public class LiteralStringValueDto
{
    public string? V { get; set; }
}

/// <summary>Sibling regression: bare dotted key still builds a table.</summary>
public class LiteralDottedInner
{
    public int B { get; set; }
}

public class LiteralDottedDto
{
    [TomlKey("a")]
    public LiteralDottedInner A { get; set; } = new();
}

/// <summary>
/// F3: a TOML literal-string quoted key (<c>'a.b' = 1</c>) is a single key whose
/// content is taken verbatim — no <c>\</c> unescaping, no dotted-key splitting —
/// exactly like a basic-string quoted key (<c>"a.b" = 1</c>). An unterminated
/// literal key is a <see cref="FormatException"/>, and a literal-string value
/// that spells <c>null</c> is still the string "null".
/// </summary>
public class LiteralQuotedKeyTests
{
    [Test]
    public async Task LiteralQuotedDottedKey_IsSingleKey()
    {
        var r = new TomlReader("'a.b' = 1\n"u8);
        r.Read();
        var tokenType = r.TokenType;
        var key = Encoding.UTF8.GetString(r.KeySpan);
        var value = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(tokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("a.b");
        await Assert.That(value).IsEqualTo("1");
    }

    [Test]
    public async Task LiteralQuotedDottedKey_DeserializesAsSingleProperty() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<LiteralQuotedDotKeyDto>(Encoding.UTF8.GetBytes("'a.b' = 1"))!
                    .Value
            )
            .IsEqualTo(1);

    [Test]
    public async Task LiteralQuotedKey_WithSpace_IsSingleKey()
    {
        var r = new TomlReader("'a b' = 2\n"u8);
        r.Read();
        var tokenType = r.TokenType;
        var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(tokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("a b");
    }

    [Test]
    public async Task LiteralQuotedKey_WithSpace_DeserializesAsSingleProperty() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<LiteralQuotedSpaceKeyDto>(Encoding.UTF8.GetBytes("'a b' = 2"))!
                    .Value
            )
            .IsEqualTo(2);

    [Test]
    public async Task LiteralQuotedKey_KeepsBackslashLiterally()
    {
        // 'a\.b' is the four characters a \ . b — a literal string has no escape
        // processing, and the dot after the backslash is still not a separator.
        var r = new TomlReader("'a\\.b' = 3\n"u8);
        r.Read();
        var tokenType = r.TokenType;
        var key = Encoding.UTF8.GetString(r.KeySpan);
        var value = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(tokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("a\\.b");
        await Assert.That(key.Length).IsEqualTo(4);
        await Assert.That(value).IsEqualTo("3");
    }

    [Test]
    public async Task LiteralQuotedKey_KeepsBackslashLiterally_Deserializes() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<LiteralQuotedBackslashKeyDto>(
                        Encoding.UTF8.GetBytes("'a\\.b' = 3")
                    )!
                    .Value
            )
            .IsEqualTo(3);

    [Test]
    public async Task LiteralQuotedKey_WithDoubleQuote_IsLiteral()
    {
        var r = new TomlReader("'a\"b' = 4\n"u8);
        r.Read();
        var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(key).IsEqualTo("a\"b");
    }

    [Test]
    public async Task UnterminatedLiteralQuotedKey_ThrowsFormatException()
    {
        var threw = false;
        try
        {
            var r = new TomlReader("'a = 1\n"u8);
            while (r.Read()) { }
        }
        catch (FormatException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    [Test]
    public async Task BasicQuotedDottedKey_StillSingleKey()
    {
        var r = new TomlReader("\"a.b\" = 1\n"u8);
        r.Read();
        var tokenType = r.TokenType;
        var key = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(tokenType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(key).IsEqualTo("a.b");
    }

    [Test]
    public async Task BareDottedKey_StillSplitsIntoTables()
    {
        var r = new TomlReader("a.b = 1\n"u8);
        r.Read();
        var firstType = r.TokenType;
        var firstKey = Encoding.UTF8.GetString(r.KeySpan);
        r.Read();
        var secondType = r.TokenType;
        var secondKey = Encoding.UTF8.GetString(r.KeySpan);
        await Assert.That(firstType).IsEqualTo(TokenType.ObjectStart);
        await Assert.That(firstKey).IsEqualTo("a");
        await Assert.That(secondType).IsEqualTo(TokenType.PropertyName);
        await Assert.That(secondKey).IsEqualTo("b");
    }

    [Test]
    public async Task BareDottedKey_StillSplitsIntoTables_Deserializes() =>
        await Assert
            .That(
                TomlSerializer.Deserialize<LiteralDottedDto>(Encoding.UTF8.GetBytes("a.b = 1"))!.A.B
            )
            .IsEqualTo(1);

    [Test]
    public async Task LiteralStringValue_nullWord_IsNotNullLiteral()
    {
        var r = new TomlReader("V = 'null'\n"u8);
        r.Read();
        var isNull = r.IsNullLiteral;
        var value = Encoding.UTF8.GetString(r.ValueSpan);
        await Assert.That(isNull).IsFalse();
        await Assert.That(value).IsEqualTo("null");
    }

    [Test]
    public async Task LiteralStringValue_nullWord_ReadsAsString() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<LiteralStringValueDto>(Encoding.UTF8.GetBytes("V = 'null'"))!
                    .V
            )
            .IsEqualTo("null");
}
