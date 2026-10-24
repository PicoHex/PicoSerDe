namespace PicoSerDe.Integration.Tests;

// The generators only support namespace-level types (GenInfrastructure skips
// types that have a containing type), so the fixtures of the brief live here
// instead of nested inside the test class.
public class NullLiteralStrDto
{
    public string? V { get; set; }
}

public class NullLiteralIntDto
{
    public int? V { get; set; }
}

public class NullLiteralNonNullInt
{
    public int V { get; set; }
}

/// <summary>
/// Review C-30: TOML/YAML/INI must accept the null literal instead of throwing,
/// and a nullable member must receive <c>null</c> rather than the literal text
/// <c>"null"</c>/<c>"~"</c>. Quoted empty strings are explicitly <em>not</em> null.
/// </summary>
public class NullLiteralTests
{
    private static NullLiteralStrDto YamlStr(string text) =>
        YamlSerializer.Deserialize<NullLiteralStrDto>(Encoding.UTF8.GetBytes(text))!;

    private static NullLiteralIntDto YamlInt(string text) =>
        YamlSerializer.Deserialize<NullLiteralIntDto>(Encoding.UTF8.GetBytes(text))!;

    private static NullLiteralStrDto TomlStr(string text) =>
        TomlSerializer.Deserialize<NullLiteralStrDto>(Encoding.UTF8.GetBytes(text))!;

    private static NullLiteralIntDto TomlInt(string text) =>
        TomlSerializer.Deserialize<NullLiteralIntDto>(Encoding.UTF8.GetBytes(text))!;

    private static NullLiteralStrDto IniStr(string text) =>
        IniSerializer.Deserialize<NullLiteralStrDto>(Encoding.UTF8.GetBytes(text))!;

    private static NullLiteralIntDto IniInt(string text) =>
        IniSerializer.Deserialize<NullLiteralIntDto>(Encoding.UTF8.GetBytes(text))!;

    [Test]
    public async Task Yaml_NullTildeAndEmpty_MapToNull()
    {
        await Assert.That(YamlStr("V: null").V).IsNull();
        await Assert.That(YamlStr("V: ~").V).IsNull();
        await Assert.That(YamlStr("V:").V).IsNull();
        await Assert.That(YamlInt("V: null").V).IsNull();
        await Assert.That(YamlInt("V:").V).IsNull();
    }

    [Test]
    public async Task Toml_And_Ini_NullLiteral_MapToNull()
    {
        await Assert.That(TomlStr("V = null").V).IsNull();
        await Assert.That(TomlInt("V = ").V).IsNull();
        await Assert.That(IniStr("V = null").V).IsNull();
        await Assert.That(IniInt("V = ").V).IsNull();
    }

    [Test]
    public void NonNullableInt_NullLiteral_Throws() =>
        Assert.Throws<FormatException>(() =>
            YamlSerializer.Deserialize<NullLiteralNonNullInt>(Encoding.UTF8.GetBytes("V: null"))
        );

    [Test]
    public async Task QuotedEmptyString_IsNotNull()
    {
        await Assert.That(TomlStr("V = \"\"").V).IsEqualTo("");
        await Assert.That(YamlStr("V: \"\"").V).IsEqualTo("");
    }

    [Test]
    public async Task QuotedNullText_StaysString()
    {
        await Assert.That(TomlStr("V = \"null\"").V).IsEqualTo("null");
        await Assert.That(YamlStr("V: \"null\"").V).IsEqualTo("null");
    }

    [Test]
    public async Task OrdinaryValues_StillParse()
    {
        await Assert.That(YamlInt("V: 5").V).IsEqualTo(5);
        await Assert.That(TomlInt("V = 5").V).IsEqualTo(5);
    }
}
