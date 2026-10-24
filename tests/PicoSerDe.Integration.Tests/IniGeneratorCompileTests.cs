namespace PicoSerDe.Integration.Tests;

public record IniRec(string Name, long Age);

public enum IniWideColor
{
    Red,
    Green,
}

[IniComment("say \"hi\"")]
public class IniCommentDto
{
    public string A { get; set; } = "a";
}

public class IniDictKeyDto
{
    public Dictionary<int, string> Map { get; set; } = new();
}

// Every kind whose EmitRead case used to hard-code '<target>.Prop' is a CS1061
// on a '__cp_N' target; this record exercises them through the ctor path.
public record IniWideRec(
    int I,
    double D,
    bool B,
    decimal M,
    System.Guid G,
    System.TimeSpan T,
    System.DateTime Dt,
    System.DateOnly Do,
    System.TimeOnly To,
    IniWideColor Color,
    List<int> Nums,
    string[] Tags
);

[IniComment("section \"quoted\" name")]
public class IniSectionQuoteDto
{
    [IniSection("a\"b\\c")]
    public IniSectionQuoteInner Inner { get; set; } = new();
}

public class IniSectionQuoteInner
{
    public string V { get; set; } = "v";
}

public class IniGeneratorCompileTests
{
    [Test]
    public async Task RecordWithLong_RoundTrips()
    {
        var text = IniSerializer.Serialize(new IniRec("a", 5));
        await Assert
            .That(IniSerializer.Deserialize<IniRec>(Encoding.UTF8.GetBytes(text))!.Age)
            .IsEqualTo(5);
    }

    [Test]
    public async Task CommentWithQuotes_Serializes() =>
        await Assert.That(IniSerializer.Serialize(new IniCommentDto())).Contains("say \"hi\"");

    [Test]
    public async Task NonStringDictKey_IsDropped() =>
        await Assert
            .That(IniSerializer.Serialize(new IniDictKeyDto { Map = { [1] = "x" } }))
            .DoesNotContain("Map");

    // The key-kind set is shared with the other generators: narrowing it for INI
    // must not narrow JSON. This DTO only triggers the INI and JSON generators
    // (TOML/YAML/MsgPack never see it — they are usage-driven), so it can assert
    // the cross-format contract without dragging in the shapes those formats
    // still cannot compile.
    [Test]
    public async Task NonStringDictKey_StillSupportedByJson()
    {
        var v = new IniDictKeyDto { Map = { [1] = "x" } };
        var json = JsonSerializer.Serialize(v);
        await Assert.That(json).Contains("Map");
        var back = JsonSerializer.Deserialize<IniDictKeyDto>(Encoding.UTF8.GetBytes(json))!;
        await Assert.That(back.Map[1]).IsEqualTo("x");
    }

    [Test]
    public async Task RecordWithEveryScalarKind_RoundTrips()
    {
        var g = System.Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var dt = new System.DateTime(2024, 1, 2, 3, 4, 5, System.DateTimeKind.Utc);
        var v = new IniWideRec(
            7,
            1.5,
            true,
            2.25m,
            g,
            System.TimeSpan.FromMinutes(90),
            dt,
            new System.DateOnly(2024, 5, 6),
            new System.TimeOnly(7, 8, 9),
            IniWideColor.Green,
            new List<int> { 1, 2 },
            new[] { "x", "y" }
        );

        var back = IniSerializer.Deserialize<IniWideRec>(
            Encoding.UTF8.GetBytes(IniSerializer.Serialize(v))
        )!;

        await Assert.That(back.I).IsEqualTo(7);
        await Assert.That(back.D).IsEqualTo(1.5);
        await Assert.That(back.B).IsTrue();
        await Assert.That(back.M).IsEqualTo(2.25m);
        await Assert.That(back.G).IsEqualTo(g);
        await Assert.That(back.T).IsEqualTo(System.TimeSpan.FromMinutes(90));
        await Assert.That(back.Dt).IsEqualTo(dt);
        await Assert.That(back.Do).IsEqualTo(new System.DateOnly(2024, 5, 6));
        await Assert.That(back.To).IsEqualTo(new System.TimeOnly(7, 8, 9));
        await Assert.That(back.Color).IsEqualTo(IniWideColor.Green);
        await Assert.That(back.Nums).IsEquivalentTo(new List<int> { 1, 2 });
        await Assert.That(back.Tags).IsEquivalentTo(new[] { "x", "y" });
    }

    [Test]
    public async Task QuotedCommentAndSectionName_RoundTrip()
    {
        var text = IniSerializer.Serialize(new IniSectionQuoteDto());
        await Assert.That(text).Contains("section \"quoted\" name");
        await Assert.That(text).Contains("[a\"b\\c]");

        var back = IniSerializer.Deserialize<IniSectionQuoteDto>(Encoding.UTF8.GetBytes(text))!;
        await Assert.That(back.Inner.V).IsEqualTo("v");
    }
}
