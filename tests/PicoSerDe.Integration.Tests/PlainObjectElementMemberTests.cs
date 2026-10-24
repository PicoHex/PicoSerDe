// TDD (F5 fix round 2): a collection of PLAIN OBJECTS must never be dropped, no matter
// which generic arguments those objects carry. Round 1 walked the element chain through
// every single-type-argument generic type, so `List<Wrapper<int?>>` looked like a nested
// `int?` sequence and lost the member to PICOSERDE004 — even though `Wrapper<int?>` is an
// object whose own members are perfectly representable.
//
// The same fixtures pin B: the YAML inner object helper used to write
// `yw.WriteNumber(value.Optional)` for an `int?` member, so the generated file did not
// compile (CS1503) — a pre-existing bug on main, not an F5 regression.
namespace PicoSerDe.Integration.Tests;

/// <summary>A plain object with a generic nullable value member.</summary>
public class PlainGenericWrap<T>
{
    public string Name { get; set; } = "";
    public T? Value { get; set; }
}

/// <summary>A plain object with a nullable value member.</summary>
public class PlainRow
{
    public string Name { get; set; } = "";
    public int? Optional { get; set; }
}

/// <summary>
/// The over-dropped shape: the element is an object that merely takes one generic
/// argument. Only YAML is exercised here (the drop rule lives in the YAML/TOML/INI
/// group); the generic element's inner helper is a YAML-only fix in this round, so the
/// fixture is never handed to another format's generator.
/// </summary>
public class PlainGenericElementHolder
{
    public string Name { get; set; } = "root";
    public List<PlainGenericWrap<int?>> Items { get; set; } = new();
    public int Count { get; set; } = 5;
}

/// <summary>B: a plain object element list whose element has a nullable value member.</summary>
public class PlainRowHolder
{
    public string Name { get; set; } = "root";
    public List<PlainRow> Rows { get; set; } = new();
    public int Count { get; set; } = 5;
}

public class PlainObjectElementMemberTests
{
    /// <summary>
    /// A: the collection whose element is an object with a nullable generic argument is
    /// NOT a nested nullable sequence — the member must be emitted and round-trip.
    /// </summary>
    [Test]
    public async Task Yaml_ListOfPlainGenericWrapper_IsEmittedAndRoundTrips()
    {
        var value = new PlainGenericElementHolder
        {
            Name = "root",
            Items =
            {
                new() { Name = "a", Value = 1 },
                new() { Name = "b", Value = null },
            },
            Count = 5,
        };

        var text = YamlSerializer.Serialize(value);
        await Assert
            .That(text)
            .Contains("Items")
            .Because("an object element list must not be dropped; yaml=" + text);

        var back = YamlSerializer.Deserialize<PlainGenericElementHolder>(
            Encoding.UTF8.GetBytes(text)
        );
        await Assert.That(back!.Items.Count).IsEqualTo(2).Because("yaml=" + text);
        await Assert.That(back.Items[0].Name).IsEqualTo("a");
        await Assert.That(back.Items[0].Value).IsEqualTo(1);
        await Assert.That(back.Items[1].Name).IsEqualTo("b");
        await Assert.That(back.Items[1].Value).IsNull();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// B: a plain object element with a nullable value member must compile (the YAML inner
    /// helper wrote `int?` into a `WriteNumber(int)` overload) and round-trip both a value
    /// and an omitted (null) member.
    /// </summary>
    [Test]
    public async Task Yaml_ListOfPlainRowsWithNullableMember_RoundTrips()
    {
        var value = new PlainRowHolder
        {
            Name = "root",
            Rows =
            {
                new() { Name = "r1", Optional = 7 },
                new() { Name = "r2", Optional = null },
            },
            Count = 5,
        };

        var text = YamlSerializer.Serialize(value);
        await Assert.That(text).Contains("Rows").Because("yaml=" + text);
        await Assert.That(text).Contains("Optional: 7").Because("yaml=" + text);
        await Assert
            .That(text.Split("Optional").Length - 1)
            .IsEqualTo(1)
            .Because("a null member is omitted, not written; yaml=" + text);

        var back = YamlSerializer.Deserialize<PlainRowHolder>(Encoding.UTF8.GetBytes(text));
        await Assert.That(back!.Rows.Count).IsEqualTo(2).Because("yaml=" + text);
        await Assert.That(back.Rows[0].Optional).IsEqualTo(7);
        await Assert.That(back.Rows[1].Optional).IsNull();
        await Assert.That(back.Count).IsEqualTo(5);
    }

    /// <summary>
    /// The drop is YAML-only logic in the shared extractor; JSON must keep the member
    /// (regression guard: the extractor fix must not narrow anything for other formats).
    /// </summary>
    [Test]
    public async Task Json_PlainRowWithNullableMember_RoundTrips()
    {
        var value = new PlainRowHolder
        {
            Rows =
            {
                new() { Name = "r1", Optional = 7 },
                new() { Name = "r2", Optional = null },
            },
        };

        var json = JsonSerializer.Serialize(value);
        await Assert.That(json).Contains("Rows").Because("json=" + json);
        var back = JsonSerializer.Deserialize<PlainRowHolder>(Encoding.UTF8.GetBytes(json));
        await Assert.That(back!.Rows[0].Optional).IsEqualTo(7);
        await Assert.That(back.Rows[1].Optional).IsNull();
    }
}
