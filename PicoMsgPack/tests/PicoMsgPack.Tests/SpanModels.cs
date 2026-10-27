namespace PicoMsgPack.Tests;

// ── Models for the Task 4b guard-rail suite ────────────────────────────────
// The writer now buffers a span of its own and only calls GetSpan/Advance when the
// span runs out. These models are chosen to hit every edge of that change: an empty
// map, a map whose payload is smaller than one span, one far larger than a span
// (several Ensure/Flush cycles), strings on both sides of the str8/str16 header
// boundaries and of the span boundary itself, and the container shapes (nested
// object, array, dictionary, binary, extension) that go through the other writers.

public class SpanEmptyModel { }

public class SpanTinyModel
{
    public string Name { get; set; } = "x";
    public int Count { get; set; } = 1;
}

public class SpanBoundaryModel
{
    // str8 header ends at 31 bytes, str16 starts at 256 — 31/32/255/256/300 pins
    // the header choice and puts the payload right on the span edge.
    public string L31 { get; set; } = new string('a', 31);
    public string L32 { get; set; } = new string('b', 32);
    public string L255 { get; set; } = new string('c', 255);
    public string L256 { get; set; } = new string('d', 256);
    public string L300 { get; set; } = new string('e', 300);
}

public class SpanWideModel
{
    // ~700 bytes of payload: three times the writer's 256-byte minimum span, so the
    // span is flushed and refetched several times in the middle of one object.
    public string S00 { get; set; } = new string('a', 32);
    public string S01 { get; set; } = new string('a', 32);
    public string S02 { get; set; } = new string('a', 32);
    public string S03 { get; set; } = new string('a', 32);
    public string S04 { get; set; } = new string('a', 32);
    public string S05 { get; set; } = new string('a', 32);
    public string S06 { get; set; } = new string('a', 32);
    public string S07 { get; set; } = new string('a', 32);
    public string S08 { get; set; } = new string('a', 32);
    public string S09 { get; set; } = new string('a', 32);
    public string S10 { get; set; } = new string('a', 32);
    public string S11 { get; set; } = new string('a', 32);
    public string S12 { get; set; } = new string('a', 32);
    public string S13 { get; set; } = new string('a', 32);
    public string S14 { get; set; } = new string('a', 32);
    public string S15 { get; set; } = new string('a', 32);
    public string S16 { get; set; } = new string('a', 32);
    public string S17 { get; set; } = new string('a', 32);
    public string S18 { get; set; } = new string('a', 32);
    public string S19 { get; set; } = new string('a', 32);
    public int N00 { get; set; } = 1_000_000;
    public int N01 { get; set; } = -1_000_000;
    public long N02 { get; set; } = 9_000_000_000L;
    public double D00 { get; set; } = 4.5;
    public bool B00 { get; set; } = true;
}

public class SpanAddressModel
{
    public string Street { get; set; } = "123 Main";
    public string City { get; set; } = "SF";
}

public class SpanNestedModel
{
    public int Id { get; set; } = 7;
    public string Name { get; set; } = "Alice";
    public SpanAddressModel Address { get; set; } = new();

    // Collections are deliberately left unset: the generated deserializer starts from
    // "null -> new List/Dictionary", so an initializer here would make it append to the
    // defaults instead of filling an empty instance (and the round-trip assertions below
    // would then compare 2 + 2 items).
    public List<string> Tags { get; set; } = null!;
    public Dictionary<string, string> Meta { get; set; } = null!;
    public List<int> Scores { get; set; } = null!;
    public byte[] Payload { get; set; } = null!;

    internal static SpanNestedModel Create() =>
        new()
        {
            Tags = ["dev", "bench"],
            Meta = new() { ["k"] = "v" },
            Scores = [1, 2, 3],
            Payload = [0x01, 0x02, 0x03],
        };
}

public class SpanHugeModel
{
    // 5000 elements spanning every integer width: the payload is ~24 KB, so the span is
    // flushed dozens of times and any dropped tail or double-committed chunk shows up in
    // the length and the digest. Unset for the same reason as SpanNestedModel.
    public List<int> Values { get; set; } = null!;

    internal static SpanHugeModel Create()
    {
        var values = new List<int>(5000);
        for (int i = 0; i < 5000; i++)
            values.Add(i % 3 == 0 ? -i * 1000 : i * 65537);
        return new SpanHugeModel { Values = values };
    }
}
