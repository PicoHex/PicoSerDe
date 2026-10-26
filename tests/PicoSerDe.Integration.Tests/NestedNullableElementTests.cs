using System.Text;

namespace PicoSerDe.Integration.Tests;

public enum NestedNullableEnum
{
    A,
    B,
}

public class NestedNullableObj
{
    public string Name { get; set; } = "";
}

/// <summary>
/// F5 review I-3: YAML has no representation for a null element inside a nested
/// sequence, yet F5 stopped dropping the member — the innermost <c>int?</c> then reached
/// <c>YamlWriter.WriteSequenceItem(int?)</c>, which has no such overload, so the whole
/// compilation failed with CS1503. Before F5 the same shape was PICOSERDE004 (warning +
/// dropped member) and compiled; the contract must stay "warn loudly and drop", never
/// "emit code that does not compile".
///
/// The dictionary shapes are listed here as well: a dictionary whose value is a nested
/// sequence cannot be represented either, and both shapes must stay compilable.
/// </summary>
public class NestedNullableElemHolder
{
    public string Name { get; set; } = "root";
    public List<List<int?>> NullableInts { get; set; } = new();
    public List<int?[]> NullableIntArrays { get; set; } = new();
    public List<List<NestedNullableEnum?>> NullableEnums { get; set; } = new();
    public List<List<NestedNullableObj?>> NullableObjs { get; set; } = new();
    public Dictionary<string, List<int?>> NullableIntMap { get; set; } = new();
    public Dictionary<string, List<int?[]>> NullableIntArrayMap { get; set; } = new();
    public int Count { get; set; } = 5;
}

/// <summary>
/// The nested shapes whose innermost element is not representable by YAML must be
/// dropped (never emitted as code that cannot compile). A fixed member is asserted by
/// name so a silent "dropped everything" regression cannot pass either.
/// </summary>
public class NestedNullableElementTests
{
    private static NestedNullableElemHolder Sample() =>
        new()
        {
            Name = "root",
            NullableInts = new()
            {
                new() { 1, null },
            },
            NullableIntArrays = new() { new int?[] { 2, null } },
            NullableEnums = new()
            {
                new() { NestedNullableEnum.A, null },
            },
            NullableObjs = new()
            {
                new()
                {
                    new() { Name = "a" },
                    null,
                },
            },
            NullableIntMap = new()
            {
                ["k"] = new() { 3, null },
            },
            NullableIntArrayMap = new() { ["k"] = new() { new int?[] { 4, null } } },
            Count = 5,
        };

    [Test]
    public async Task Yaml_NestedNullableElementMembers_AreDroppedWithTheDiagnostic()
    {
        var text = YamlSerializer.Serialize(Sample());
        await Assert.That(text).Contains("Name: root").Because("actual=" + text);
        await Assert.That(text).Contains("Count: 5").Because("actual=" + text);
        foreach (
            var member in new[]
            {
                "NullableInts",
                "NullableIntArrays",
                "NullableEnums",
                "NullableObjs",
                "NullableIntMap",
                "NullableIntArrayMap",
            }
        )
            await Assert.That(text).DoesNotContain(member).Because("dropped member " + member);
    }

    /// <summary>
    /// Reading a document that carries a (dropped) member name must not throw: the
    /// member is skipped like any other unknown key (its value here is a scalar, the
    /// generated skip path does not descend into blocks).
    /// </summary>
    [Test]
    public async Task Yaml_NestedNullableElementMembers_ReadBackAsDefaults()
    {
        var doc = "Name: doc\nNullableInts: []\nCount: 7\n"u8.ToArray();
        var back = YamlSerializer.Deserialize<NestedNullableElemHolder>(doc);
        await Assert.That(back!.Name).IsEqualTo("doc");
        await Assert.That(back.Count).IsEqualTo(7);
        await Assert.That(back.NullableInts).IsEmpty();
    }

    /// <summary>
    /// JSON can express a null element, so the drop must stay format-specific: the same
    /// fixture keeps its nested nullable lists there (regression guard for the shared
    /// extractor). R2 Task 3 also implemented the dict VALUE position, so the two
    /// dictionary-of-nested-list members are emitted in json now and have to round-trip —
    /// they used to be dropped in every format, and "emitted" would be worth nothing
    /// without the value-level assertion.
    /// </summary>
    [Test]
    public async Task Json_NestedNullableElementMembers_RoundTrip()
    {
        var json = JsonSerializer.Serialize(Sample());
        var back = JsonSerializer.Deserialize<NestedNullableElemHolder>(
            Encoding.UTF8.GetBytes(json)
        );
        await Assert.That(back!.NullableInts.Count).IsEqualTo(1).Because("json=" + json);
        await Assert.That(back.NullableInts[0].Count).IsEqualTo(2);
        await Assert.That(back.NullableInts[0][1]).IsNull();
        await Assert.That(back.NullableObjs[0][1]).IsNull();

        await Assert.That(json).Contains("NullableIntMap").Because("json=" + json);
        await Assert.That(back.NullableIntMap.Count).IsEqualTo(1).Because("json=" + json);
        await Assert.That(back.NullableIntMap["k"].Count).IsEqualTo(2);
        await Assert.That(back.NullableIntMap["k"][0]).IsEqualTo(3);
        await Assert.That(back.NullableIntMap["k"][1]).IsNull();

        await Assert.That(json).Contains("NullableIntArrayMap").Because("json=" + json);
        await Assert.That(back.NullableIntArrayMap.Count).IsEqualTo(1);
        await Assert.That(back.NullableIntArrayMap["k"][0].Length).IsEqualTo(2);
        await Assert.That(back.NullableIntArrayMap["k"][0][1]).IsNull();
    }
}
