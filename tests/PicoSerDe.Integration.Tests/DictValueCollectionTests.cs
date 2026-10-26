namespace PicoSerDe.Integration.Tests;

/// <summary>S10 — the VALUE of a dictionary member is a collection.</summary>
public class RvDictValueList
{
    public string Name { get; set; } = "";
    public Dictionary<string, List<int>> Map { get; set; } = new();
}

/// <summary>
/// S10 (<c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>): the dictionary's VALUE is a collection.
/// <para>
/// This shape used to be dropped in ALL five formats by an unconditional kind test in the
/// extractor (<c>vk is "list" or "array"</c>, plus a second literal for the seven extended
/// collection kinds) — the same "kind literal instead of a table lookup" trap R2 Task 2
/// removed from the ELEMENT position, in its second copy. So the cell could say Supported
/// while the member disappeared from the generated code, and the extended-kind half of the
/// drop was even reported against <c>ExtendedKindAsElement</c> — a row that already said
/// Supported (T2 review I-2).
/// </para>
/// <para>
/// R2 Task 3 made the site ask <c>Decision.DictValueCollection</c> and taught the four
/// formats the value position: the four are round-trips below, ini stays ⛔ (its sections
/// carry no nesting at all, so S10 × ini is a format FACT — spec §3 — not a gap).
/// </para>
/// </summary>
public class DictValueCollectionTests
{
    // ── the four formats that can express it ─────────────────────────────────────────

    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Dict_value_collection_round_trips(string format)
    {
        await AssertDictRoundTrip(format, new());
        await AssertDictRoundTrip(
            format,
            new()
            {
                ["k"] = new() { 1, 2 },
            }
        );
        await AssertDictRoundTrip(
            format,
            new()
            {
                ["a"] = new() { 2147483647 },
                ["e"] = new(),
                ["z"] = new() { -1, 0 },
            }
        );
    }

    /// <summary>
    /// The empty value and the empty LIST VALUE are different things: <c>{"e":[]}</c> must
    /// come back as a key with an empty list, not as a missing key. Element count and order
    /// are this shape's equivalence relation (a List, not a Set), so the assertion is
    /// order-sensitive on purpose.
    /// </summary>
    [Test]
    [Arguments("json")]
    [Arguments("msgpack")]
    [Arguments("toml")]
    [Arguments("yaml")]
    public async Task Empty_list_value_survives_as_an_empty_list(string format)
    {
        var back = RoundTrip(
            format,
            new()
            {
                Name = "n",
                Map = new() { ["e"] = new() },
            }
        );

        await Assert.That(back.Map.ContainsKey("e")).IsTrue().Because(format);
        await Assert.That(back.Map["e"]).IsEmpty().Because(format);
        await Assert.That(back.Name).IsEqualTo("n").Because(format);
    }

    /// <summary>
    /// YAML's resumable reader takes the OTHER alignment once a member has a block
    /// sequence: a sequence arm ends on the wrapper's <c>ObjectEnd</c>, one <c>Read()</c>
    /// away from the next key, while a scalar arm never reads at all. Fed one byte at a
    /// time this exercises that difference on every entry — the plain round trip above only
    /// ever sees the whole document at once, so it cannot tell the two apart.
    /// </summary>
    [Test]
    public async Task Yaml_streaming_read_survives_a_dict_of_sequences()
    {
        var model = new RvDictValueList
        {
            Name = "n",
            Map = new()
            {
                ["a"] = new() { 2147483647 },
                ["e"] = new(),
                ["z"] = new() { -1, 0 },
            },
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes(YamlSerializer.Serialize(model));
        using var stream = new OneByteStream(bytes);

        var back = await YamlSerializer.DeserializeFromStreamAsync<RvDictValueList>(stream);

        await Assert.That(back!.Name).IsEqualTo("n");
        await Assert.That(back.Map.Count).IsEqualTo(3).Because("all three entries survive");
        await Assert.That(string.Join(",", back.Map["a"])).IsEqualTo("2147483647");
        await Assert.That(back.Map["e"]).IsEmpty();
        await Assert.That(string.Join(",", back.Map["z"])).IsEqualTo("-1,0");
    }

    // ── ini: still dropped, and dropped as a FACT ───────────────────────────────────

    /// <summary>
    /// ini's member position is flat (no nesting inside a member), so S10 × ini stays ⛔
    /// and the member is dropped. The DIAGNOSTIC side of that drop is pinned in
    /// <see cref="SilentDropDiagnosticTests.Dict_of_list_is_a_fact_in_ini"/> — the message
    /// has to say "the ini format cannot", not "not implemented yet".
    /// </summary>
    [Test]
    public async Task Ini_still_drops_the_member()
    {
        var back = IniSerializer.Deserialize<RvDictValueList>(
            IniSerializer.SerializeToUtf8Bytes(Sample())
        )!;

        await Assert.That(back.Name).IsEqualTo("n");
        await Assert.That(back.Map.Count).IsEqualTo(0);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────

    private static RvDictValueList Sample() =>
        new()
        {
            Name = "n",
            Map = new()
            {
                ["k"] = new() { 1, 2 },
            },
        };

    private static async Task AssertDictRoundTrip(
        string format,
        Dictionary<string, List<int>> value
    )
    {
        var back = RoundTrip(format, new() { Name = "n", Map = value });

        await Assert.That(back.Name).IsEqualTo("n").Because(format);
        await Assert
            .That(back.Map.Count)
            .IsEqualTo(value.Count)
            .Because($"{format}: key count for {Describe(value)}");
        foreach (var (key, expected) in value)
        {
            await Assert
                .That(back.Map.ContainsKey(key))
                .IsTrue()
                .Because($"{format}: key '{key}' missing from {Describe(back.Map)}");
            // ORDER-SENSITIVE on purpose: a List's equivalence relation is order + length,
            // so `IsEquivalentTo` (order-insensitive) would accept a reversed value.
            await Assert
                .That(string.Join(",", back.Map[key]))
                .IsEqualTo(string.Join(",", expected))
                .Because($"{format}: value of '{key}' for {Describe(value)}");
        }
    }

    private static string Describe(Dictionary<string, List<int>> value) =>
        "{"
        + string.Join(", ", value.Select(kv => $"{kv.Key}:[{string.Join(",", kv.Value)}]"))
        + "}";

    // Written out per format ON PURPOSE: the generators are usage-driven, so a generic
    // `RoundTrip<T>` call site only ever shows them `T` and no serializer is registered.

    /// <summary>A stream that hands out one byte per read: the harshest chunking there is.</summary>
    private sealed class OneByteStream(byte[] data) : Stream
    {
        private int _pos;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _pos;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length || count == 0)
                return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private static RvDictValueList RoundTrip(string format, RvDictValueList model) =>
        format switch
        {
            "json" => JsonSerializer.Deserialize<RvDictValueList>(
                JsonSerializer.SerializeToUtf8Bytes(model)
            )!,
            "msgpack" => MsgPackSerializer.Deserialize<RvDictValueList>(
                MsgPackSerializer.SerializeToUtf8Bytes(model)
            )!,
            "toml" => TomlSerializer.Deserialize<RvDictValueList>(
                TomlSerializer.SerializeToUtf8Bytes(model)
            )!,
            _ => YamlSerializer.Deserialize<RvDictValueList>(
                YamlSerializer.SerializeToUtf8Bytes(model)
            )!,
        };
}
