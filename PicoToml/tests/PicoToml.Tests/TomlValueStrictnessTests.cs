namespace PicoToml.Tests;

// Models at namespace level (SG skips nested types)
public class StrictNumDto
{
    public int Hex { get; set; }
    public int Under { get; set; }
}

public class StrictFlagDto
{
    public bool Flag { get; set; }
}

public class StrictArrayDto
{
    public int[] Tags { get; set; } = [];
}

public class StrictInlineDto
{
    public Dictionary<string, int> Map { get; set; } = new();
}

public class StrictQuotedKeyDto
{
    [TomlKey("a.b")]
    public int Value { get; set; }
}

public class StrictDottedInner
{
    public int B { get; set; }
}

public class StrictDottedDto
{
    [TomlKey("a")]
    public StrictDottedInner A { get; set; } = new();
}

/// <summary>
/// C-12 / C-13: a bare value token must be consumed and parsed in full — radix
/// prefixes and '_' separators included — an inline comment must not leak into
/// the value, and a quoted key's content must never be split as a dotted key.
/// </summary>
public class TomlValueStrictnessTests
{
    [Test]
    public async Task HexAndUnderscore_ParseExactly()
    {
        var v = TomlSerializer.Deserialize<StrictNumDto>(
            Encoding.UTF8.GetBytes("Hex = 0x1F\nUnder = 1_000")
        )!;
        await Assert.That(v.Hex).IsEqualTo(31);
        await Assert.That(v.Under).IsEqualTo(1000);
    }

    [Test]
    public async Task TrailingComment_IsIgnored() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<StrictFlagDto>(Encoding.UTF8.GetBytes("Flag = true # note"))!
                    .Flag
            )
            .IsTrue();

    [Test]
    public async Task ArrayWithTrailingComment_IsIgnored()
    {
        var v = TomlSerializer.Deserialize<StrictArrayDto>(
            Encoding.UTF8.GetBytes("Tags = [1, 2] # c")
        )!;
        await Assert.That(v.Tags.Length).IsEqualTo(2);
        await Assert.That(v.Tags[0]).IsEqualTo(1);
        await Assert.That(v.Tags[1]).IsEqualTo(2);
    }

    [Test]
    public async Task ArrayWithInnerComment_IsIgnored()
    {
        // The comment sits *inside* the brackets, so the array element scan has
        // to truncate it — the key/value scanner never sees this text.
        var v = TomlSerializer.Deserialize<StrictArrayDto>(
            Encoding.UTF8.GetBytes("Tags = [1, 2 # c\n]")
        )!;
        await Assert.That(v.Tags.Length).IsEqualTo(2);
        await Assert.That(v.Tags[1]).IsEqualTo(2);
    }

    [Test]
    public async Task InlineTableWithTrailingComment_IsIgnored()
    {
        var v = TomlSerializer.Deserialize<StrictInlineDto>(
            Encoding.UTF8.GetBytes("Map = { k = 1 } # c")
        )!;
        await Assert.That(v.Map["k"]).IsEqualTo(1);
    }

    [Test]
    public async Task InlineTableWithInnerComment_IsIgnored()
    {
        var v = TomlSerializer.Deserialize<StrictInlineDto>(
            Encoding.UTF8.GetBytes("Map = { k = 1 # c\n }")
        )!;
        await Assert.That(v.Map["k"]).IsEqualTo(1);
    }

    [Test]
    public async Task QuotedDottedKey_IsSingleKey() =>
        await Assert
            .That(
                TomlSerializer
                    .Deserialize<StrictQuotedKeyDto>(Encoding.UTF8.GetBytes("\"a.b\" = 1"))!
                    .Value
            )
            .IsEqualTo(1);

    [Test]
    public async Task DottedKey_StillSplitsIntoTables()
    {
        var v = TomlSerializer.Deserialize<StrictDottedDto>(Encoding.UTF8.GetBytes("a.b = 2"))!;
        await Assert.That(v.A.B).IsEqualTo(2);
    }
}
