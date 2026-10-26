using System.Text;

namespace PicoToml.Tests;

/// <summary>The payload of the nested object list, chosen to carry R5 boundary values.</summary>
public class NestedRowDto
{
    public string Name { get; set; } = "";
    public int N { get; set; }
}

public class NestedRowsDto
{
    public List<List<NestedRowDto>> V { get; set; } = new();
}

/// <summary>
/// R2 Task 6 — spec §3 S4 × TOML (<c>List&lt;List&lt;Dto&gt;&gt;</c>).
/// <para>
/// TOML can hold this: an inline array of inline arrays whose innermost elements are
/// inline tables (<c>V = [[{ Name = "a", N = 1 }], [{ Name = "b" }]]</c>) — the spec's
/// own §9 probe (<c>tomllib.loads("V = [[{a = 1}], [{b = 2}]]")</c>) accepts exactly that
/// shape. The extractor used to drop the member (<c>NestedObjectContainer</c> said
/// DropMember, and the leaf-kind list had no <c>object</c> case) because the writer had no
/// "inline table as an ARRAY ELEMENT" form: <c>WriteStartInlineTable</c> always wants a
/// key, and the array-of-tables form (<c>[[V]]</c>) cannot nest inside another array.
/// </para>
/// <para>
/// So this pins three things a future edit must not break: the inline-table element form,
/// the empty inner list (an element with no values is <c>[]</c>, not a missing element),
/// and the round trip of the values themselves.
/// </para>
/// </summary>
public class TomlNestedObjectArrayTests
{
    [Test]
    public async Task NestedObjectArray_RoundTrips()
    {
        var dto = new NestedRowsDto
        {
            V =
            [
                [new NestedRowDto { Name = "a", N = 1 }],
                [
                    new NestedRowDto { Name = "p|ipe \"q\" line\nbreak\\slash", N = int.MaxValue },
                    new NestedRowDto { Name = "", N = -1 },
                ],
                [],
            ],
        };

        var text = TomlSerializer.Serialize(dto);

        // The form: one bracket pair per level, an inline table per row.
        await Assert.That(text).Contains("V = [[").Because(text);
        await Assert.That(text).Contains("V = [[{").Because(text);
        await Assert.That(text).Contains("}]").Because(text);

        var back = TomlSerializer.Deserialize<NestedRowsDto>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V.Count).IsEqualTo(3);
        await Assert.That(back.V[0].Count).IsEqualTo(1);
        await Assert.That(back.V[0][0].Name).IsEqualTo("a");
        await Assert.That(back.V[0][0].N).IsEqualTo(1);
        await Assert.That(back.V[1].Count).IsEqualTo(2);
        await Assert.That(back.V[1][0].Name).IsEqualTo("p|ipe \"q\" line\nbreak\\slash");
        await Assert.That(back.V[1][0].N).IsEqualTo(int.MaxValue);
        await Assert.That(back.V[1][1].N).IsEqualTo(-1);
        // The empty inner list stays an ELEMENT: "an empty list" and "no element" are
        // different values (spec R1′'s null three-state rule, applied to nesting).
        await Assert.That(back.V[2].Count).IsEqualTo(0);
    }

    [Test]
    public async Task EmptyNestedObjectArray_RoundTrips()
    {
        var text = TomlSerializer.Serialize(new NestedRowsDto());

        await Assert.That(text).Contains("V = []").Because(text);

        var back = TomlSerializer.Deserialize<NestedRowsDto>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V.Count).IsEqualTo(0);
    }

    /// <summary>
    /// Hand-written TOML (not produced by our writer) must read too: the reader is the
    /// lenient half of R1″ and an inline table inside a nested array is a plain TOML 1.0
    /// value, not our invention.
    /// </summary>
    [Test]
    public async Task HandWrittenNestedInlineTables_AreRead()
    {
        const string text = "V = [[{ Name = \"a\", N = 1 }, { Name = \"b\", N = 2 }], []]\n";

        var back = TomlSerializer.Deserialize<NestedRowsDto>(Encoding.UTF8.GetBytes(text));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.V.Count).IsEqualTo(2);
        await Assert.That(back.V[0].Count).IsEqualTo(2);
        await Assert.That(back.V[0][1].Name).IsEqualTo("b");
        await Assert.That(back.V[0][1].N).IsEqualTo(2);
        await Assert.That(back.V[1].Count).IsEqualTo(0);
    }

    /// <summary>
    /// The streaming entry point runs the sequence-mode reader, a second copy of the array
    /// scan — a chunked payload has to produce the same tree.
    /// </summary>
    [Test]
    public async Task NestedObjectArray_RoundTripsThroughTheStreamingEntryPoint()
    {
        var dto = new NestedRowsDto
        {
            V =
            [
                [new NestedRowDto { Name = "a", N = 7 }],
                [],
            ],
        };
        var bytes = TomlSerializer.SerializeToUtf8Bytes(dto);

        var back = await TomlSerializer.DeserializeFromStreamAsync<NestedRowsDto>(
            new MemoryStream(bytes)
        );

        await Assert.That(back.V.Count).IsEqualTo(2);
        await Assert.That(back.V[0].Count).IsEqualTo(1);
        await Assert.That(back.V[0][0].Name).IsEqualTo("a");
        await Assert.That(back.V[0][0].N).IsEqualTo(7);
        await Assert.That(back.V[1].Count).IsEqualTo(0);
    }
}
