using System.Text;

namespace PicoSerDe.Integration.Tests;

// ── S11a models: self recursion (Node.Child : Node) ───────────────────────────────────
// One model per format on purpose: the probe (tests/FormatCapabilities.Probe) proved that
// a shared model would let one format's generated helper leak into another's, and each
// cell must be measurable on its own.

public class W5TomlNode
{
    public int Value { get; set; }

    public W5TomlNode? Child { get; set; }
}

public class W5YamlNode
{
    public int Value { get; set; }

    public W5YamlNode? Child { get; set; }
}

// ── S11b models: mutual recursion (A.B : B, B.A : A) ──────────────────────────────────

public class W5TomlA
{
    public string Name { get; set; } = "";

    public W5TomlB? B { get; set; }
}

public class W5TomlB
{
    public int N { get; set; }

    public W5TomlA? A { get; set; }
}

public class W5YamlA
{
    public string Name { get; set; } = "";

    public W5YamlB? B { get; set; }
}

public class W5YamlB
{
    public int N { get; set; }

    public W5YamlA? A { get; set; }
}

/// <summary>
/// R2 Task 5: the four recursion cells the model has always claimed as "debt" are now
/// real — S11a/S11b × {toml, yaml}. Before this task the extractor asked
/// <c>Decision.RecursiveSelf</c> for BOTH cycle kinds, so a mutual cycle was dropped in
/// YAML where the table said Supported, and a self cycle was dropped in TOML/YAML where
/// the facts said the formats can hold one (spec §3: TOML nested tables, YAML nested
/// mappings).
/// <para>
/// The assertions are TREE SHAPE, not "the member is present" (spec R1′: recursion is
/// compared by tree isomorphism): a format that keeps one level and truncates the rest
/// would pass a contains-check and fail these. The wire assertions pin the ENCODING the
/// formats actually use for the depth — TOML must qualify nested table headers
/// (<c>[Child.Child]</c>, not a bare <c>[Child]</c> repeated), YAML must nest mappings.
/// </para>
/// </summary>
public class RecursionRoundTripTests
{
    /// <summary>Three levels: the deepest value a "one nested level" emitter would truncate.</summary>
    private static W5TomlNode TomlSpine() =>
        new()
        {
            Value = 1,
            Child = new W5TomlNode
            {
                Value = 2,
                Child = new W5TomlNode { Value = 3 },
            },
        };

    private static W5YamlNode YamlSpine() =>
        new()
        {
            Value = 1,
            Child = new W5YamlNode
            {
                Value = 2,
                Child = new W5YamlNode { Value = 3 },
            },
        };

    private static W5TomlA TomlPair() =>
        new()
        {
            Name = "root",
            B = new W5TomlB
            {
                N = 7,
                A = new W5TomlA
                {
                    Name = "mid",
                    B = new W5TomlB
                    {
                        N = -1,
                        A = new W5TomlA { Name = "leaf" },
                    },
                },
            },
        };

    private static W5YamlA YamlPair() =>
        new()
        {
            Name = "root",
            B = new W5YamlB
            {
                N = 7,
                A = new W5YamlA
                {
                    Name = "mid",
                    B = new W5YamlB
                    {
                        N = -1,
                        A = new W5YamlA { Name = "leaf" },
                    },
                },
            },
        };

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    // ── S11a × toml ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Toml_SelfRecursion_RoundTripsTheWholeSpine()
    {
        var wire = TomlSerializer.Serialize(TomlSpine());

        // TOML holds recursion as nested tables, and a nested table is addressed by its
        // DOTTED path: a second bare `[Child]` would be a duplicate table, and the reader
        // would either overwrite the first one or refuse the document.
        await Assert.That(wire).Contains("[Child]").Because(wire);
        await Assert.That(wire).Contains("[Child.Child]").Because(wire);

        var back = TomlSerializer.Deserialize<W5TomlNode>(Utf8(wire));

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Value).IsEqualTo(1);
        await Assert.That(back.Child).IsNotNull();
        await Assert.That(back.Child!.Value).IsEqualTo(2).Because($"wire: {wire}");
        await Assert.That(back.Child.Child).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.Child.Child!.Value).IsEqualTo(3);
        await Assert.That(back.Child.Child.Child).IsNull();
    }

    [Test]
    public async Task Toml_SelfRecursion_EmitsNothingForANullTail()
    {
        // The empty tail is the R1′ null shape: an absent member (not an empty table).
        var wire = TomlSerializer.Serialize(new W5TomlNode { Value = 5 });

        await Assert.That(wire).Contains("Value = 5").Because(wire);
        await Assert.That(wire.Contains("[Child]", StringComparison.Ordinal)).IsFalse();

        var back = TomlSerializer.Deserialize<W5TomlNode>(Utf8(wire));
        await Assert.That(back!.Value).IsEqualTo(5);
        await Assert.That(back.Child).IsNull();
    }

    // ── S11a × yaml ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Yaml_SelfRecursion_RoundTripsTheWholeSpine()
    {
        var wire = YamlSerializer.Serialize(YamlSpine());

        var back = YamlSerializer.Deserialize<W5YamlNode>(Utf8(wire));

        await Assert.That(back).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back!.Value).IsEqualTo(1);
        await Assert.That(back.Child).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.Child!.Value).IsEqualTo(2);
        await Assert.That(back.Child.Child).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.Child.Child!.Value).IsEqualTo(3);
        await Assert.That(back.Child.Child.Child).IsNull();
    }

    // ── S11b × toml ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Toml_MutualRecursion_RoundTripsTheWholeSpine()
    {
        var wire = TomlSerializer.Serialize(TomlPair());

        // A mutual cycle in TOML is a table inside a table inside a table: the header has
        // to carry the ancestor path, otherwise the second `[A]` reopens the ROOT table
        // (that is D-5: the root member gets overwritten by the child's).
        await Assert.That(wire).Contains("[B]").Because(wire);
        await Assert.That(wire).Contains("[B.A]").Because(wire);
        await Assert.That(wire).Contains("[B.A.B]").Because(wire);

        var back = TomlSerializer.Deserialize<W5TomlA>(Utf8(wire));

        await Assert.That(back).IsNotNull();
        await Assert
            .That(back!.Name)
            .IsEqualTo("root")
            .Because($"D-5: the root member survives | wire: {wire}");
        await Assert.That(back.B).IsNotNull();
        await Assert.That(back.B!.N).IsEqualTo(7);
        await Assert.That(back.B.A).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A!.Name).IsEqualTo("mid");
        await Assert.That(back.B.A.B).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A.B!.N).IsEqualTo(-1);
        await Assert.That(back.B.A.B.A).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A.B.A!.Name).IsEqualTo("leaf");
        await Assert.That(back.B.A.B.A.B).IsNull();
    }

    // ── S11b × yaml ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Yaml_MutualRecursion_RoundTripsTheWholeSpine()
    {
        var wire = YamlSerializer.Serialize(YamlPair());

        var back = YamlSerializer.Deserialize<W5YamlA>(Utf8(wire));

        await Assert.That(back).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back!.Name).IsEqualTo("root");
        await Assert.That(back.B).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B!.N).IsEqualTo(7);
        await Assert.That(back.B.A).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A!.Name).IsEqualTo("mid");
        await Assert.That(back.B.A.B).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A.B!.N).IsEqualTo(-1);
        await Assert.That(back.B.A.B.A).IsNotNull().Because($"wire: {wire}");
        await Assert.That(back.B.A.B.A!.Name).IsEqualTo("leaf");
        await Assert.That(back.B.A.B.A.B).IsNull();
    }

    /// <summary>
    /// The streaming reader shares the nested-table read with the span reader, so a chunked
    /// document has to survive too — otherwise the flip would only be true for callers who
    /// happen to read from a span.
    /// </summary>
    [Test]
    public async Task Toml_SelfRecursion_Streaming_Chunked()
    {
        var wire = Encoding.UTF8.GetBytes(TomlSerializer.Serialize(TomlSpine()));
        foreach (var chunk in new[] { 1, 9, 128 })
        {
            using var stream = new SkChunkedStream(wire, chunk);
            var back = await TomlSerializer.DeserializeFromStreamAsync<W5TomlNode>(stream);
            await Assert
                .That(back!.Child!.Child!.Value)
                .IsEqualTo(3)
                .Because($"toml chunk={chunk}");
        }
    }

    /// <summary>
    /// The other half of a flip: the cells that must NOT move. INI is flat (spec §3: S11a /
    /// S11b are ❌ there), so its recursion member is dropped — and, because it is a format
    /// fact rather than our debt, the diagnostic must say so (⛔, not ⏸).
    /// </summary>
    [Test]
    public async Task Ini_Recursion_StaysADroppedMember()
    {
        var wire = IniSerializer.Serialize(new W5IniNode { Value = 1 });

        await Assert.That(wire).Contains("Value").Because(wire);
        await Assert
            .That(wire.Contains("Child", StringComparison.Ordinal))
            .IsFalse()
            .Because($"INI has no nesting at all (S11a x ini is a fact): {wire}");
    }
}

public class W5IniNode
{
    public int Value { get; set; }

    public W5IniNode? Child { get; set; }
}
