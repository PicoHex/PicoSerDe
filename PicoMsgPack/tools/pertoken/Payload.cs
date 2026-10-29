using PicoSerDe.Core;

namespace PicoMsgPack.Tools.PerToken;

/// <summary>
/// One payload to measure, with the token ledger it must produce. The ledger is <i>asserted</i> before the
/// payload is timed, so "per-token" always divides by the token count the reader actually produced rather than
/// by a number copied out of a document.
/// </summary>
/// <param name="Key">Short name used in the <c>SUMMARY</c> line.</param>
/// <param name="Origin">Where the shape comes from — printed with the numbers so they cannot be quoted bare.</param>
/// <param name="Bytes">The MessagePack payload.</param>
/// <param name="Tokens">Tokens <c>Read()</c> returns for one pass.</param>
/// <param name="IntTokens">Tokens whose <c>TryGetInt32</c> succeeds (what a fused int read can serve).</param>
/// <param name="IntSum">Their sum — a wrong value means an arm walked a different token stream.</param>
internal sealed record Shape(
    string Key,
    string Origin,
    byte[] Bytes,
    int Tokens,
    int IntTokens,
    long IntSum
);

/// <summary>
/// The two payloads this harness measures. Both are the real wire bytes of a real model, and both divide by
/// the number of <c>Read()</c> calls they provoke.
/// </summary>
internal static class Shapes
{
    /// <summary>
    /// <b>The mandated shape</b> (spec §1.2): <c>CollectionPoco</c> / <c>MsgCollection</c> — a 2-entry map
    /// holding a 100-element int array and a 1-entry string map. It is the model whose wire stream §1.2
    /// enumerates by hand and the model the published <c>Collection — Deserialize</c> row deserializes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Where spec §1.2's hand count differs from what the reader produces</b> (found by this harness; the
    /// arithmetic is unaffected to within 3 %, so it is recorded rather than "corrected"):
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// §1.2 counts <b>value</b> tokens (107) and omits the three container-close tokens — <c>Scores</c>'s
    /// <c>ArrayEnd</c> and the two <c>ObjectEnd</c>s are each a separate <c>Read()</c>. A per-token cost must
    /// divide by the number of <c>Read()</c> calls, so this harness divides by <b>110</b>.
    /// </item>
    /// <item>
    /// §1.2 lists the map keys as strings (<c>"Scores"</c>, <c>"Metadata"</c>, four string tokens in total).
    /// With <c>[MsgPackKey(n)]</c> the generator writes the keys as <b>integer indices</b>, so the payload
    /// carries two 1-byte <c>fixint</c> keys and only two string tokens (<c>"source"</c>, <c>"benchmark"</c>):
    /// 124 bytes, and <b>102</b> int tokens, not 100.
    /// </item>
    /// </list>
    /// </remarks>
    public static Shape Collection { get; } =
        new(
            "collection",
            "Collection shape (spec §1.2 ledger) — map{ Scores:int[100], Metadata:{source:benchmark} }",
            MsgPackSerializer.SerializeToUtf8Bytes(
                new CollectionShape
                {
                    Scores = [.. Enumerable.Range(0, 100)],
                    Metadata = new Dictionary<string, string> { ["source"] = "benchmark" },
                }
            ),
            Tokens: 110,
            IntTokens: 102,
            IntSum: 4951
        );

    /// <summary>
    /// <b>T6's shape</b>: a bare <c>fixarray16(1000)</c> of <c>fixint</c>s — the payload the deleted
    /// <c>.superpowers/sdd/t6-int32</c> harness measured its "per-token 10 %" on (report §5.4). Repeated here
    /// so the two figures can be compared on the same machine instead of across sessions: it isolates what the
    /// mixed <c>Collection</c> shape pays on its 8 non-int tokens, where the fused read must fall back.
    /// </summary>
    /// <remarks>
    /// Hand-built because it is a wire shape, not a model: three header bytes then 1000 fixints cycling
    /// 0..99 (so the sum is 10 × 4950 and a wrong stream is detectable). 1002 tokens, not 1001: the report
    /// counted the header plus the 1000 elements and — like spec §1.2 — left out the <c>ArrayEnd</c>, which
    /// is one more <c>Read()</c>.
    /// </remarks>
    public static Shape FixInts1000 { get; } = BuildFixInts1000();

    public static IReadOnlyList<Shape> All { get; } = [Collection, FixInts1000];

    private static Shape BuildFixInts1000()
    {
        var bytes = new byte[3 + 1000];
        bytes[0] = 0xDC; // array16
        bytes[1] = 0x03;
        bytes[2] = 0xE8; // 1000 elements
        for (var i = 0; i < 1000; i++)
            bytes[3 + i] = (byte)(i % 100); // fixint 0..99

        return new Shape(
            "fixints1000",
            "T6's per-token shape — fixarray16(1000) of fixints (t6-int32 report §5.4)",
            bytes,
            Tokens: 1002,
            IntTokens: 1000,
            IntSum: 49_500
        );
    }
}

/// <summary>
/// Walk a payload once with the plain two-stage path and report what was actually in it.
/// </summary>
internal static class Payload
{
    /// <summary>
    /// Verify the payload against its ledger. Throws when the stream disagrees — a drifted payload must stop
    /// the run, never quietly become the new denominator.
    /// </summary>
    /// <returns>Token-type histogram, printed with the shape header so a run is auditable.</returns>
    public static IReadOnlyDictionary<string, int> Verify(Shape shape)
    {
        var reader = new MsgPackReader(shape.Bytes);
        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var tokens = 0;
        var intTokens = 0;
        long intSum = 0;

        while (reader.Read())
        {
            tokens++;
            var name = reader.TokenType.ToString();
            histogram[name] = histogram.TryGetValue(name, out var seen) ? seen + 1 : 1;

            if (reader.TokenType is TokenType.Int32 or TokenType.Int64)
            {
                intTokens++;
                if (reader.TryGetInt32(out var v))
                    intSum += v;
            }
        }

        if (tokens != shape.Tokens)
            throw new InvalidOperationException(
                $"payload ledger drift ({shape.Key}): Read() produced {tokens} tokens, the ledger says "
                    + $"{shape.Tokens}. A changed payload changes the denominator of every number here — fix "
                    + "the ledger deliberately, do not let the run continue."
            );
        if (intTokens != shape.IntTokens || intSum != shape.IntSum)
            throw new InvalidOperationException(
                $"payload ledger drift ({shape.Key}): {intTokens} int tokens summing {intSum}; the ledger says "
                    + $"{shape.IntTokens} summing {shape.IntSum} — the arms would not be walking the same token "
                    + "stream."
            );

        return histogram;
    }

    /// <summary>
    /// Print the token stream, one line per <c>Read()</c> — the ledger from the other direction: instead of
    /// "110 tokens", the 110 lines themselves. <c>--dump</c> is what makes the per-token count checkable
    /// without a debugger.
    /// </summary>
    public static void Dump(byte[] payload)
    {
        var reader = new MsgPackReader(payload);
        var index = 0;
        while (reader.Read())
        {
            Console.WriteLine(
                $"{index, 4}|{reader.TokenType, -12}|consumed={reader.BytesConsumed, 5}|depth={reader.Depth}"
            );
            index++;
        }

        Console.WriteLine($"{index, 4}|<Read() returned false>");
    }
}

/// <summary>
/// The measured shape: <c>CollectionPoco</c>'s two properties, with MessagePack keys so the wire stream is a
/// 2-entry map. Deliberately not the shared <c>BenchModels.cs</c> type: that file carries no format attributes
/// and is compiled into the benchmark projects, not into tools.
/// </summary>
[PicoMsgPackSerializable]
public sealed class CollectionShape
{
    [MsgPackKey(0)]
    public List<int> Scores { get; set; } = [];

    [MsgPackKey(1)]
    public Dictionary<string, string> Metadata { get; set; } = [];
}
