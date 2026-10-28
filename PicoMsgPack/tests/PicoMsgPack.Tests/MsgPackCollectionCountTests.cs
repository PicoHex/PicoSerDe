using System.Globalization;

namespace PicoMsgPack.Tests;

/// <summary>
/// Task T5 (fused token read, spec §6 S1 — the count half): pins
/// <see cref="IFusedTokenReader.HasCountedCollections"/> and
/// <see cref="IFusedTokenReader.TryGetCollectionCount"/> for MessagePack.
/// <para>
/// <b>What the count means (spec §3.1 "Contract", §3.3 the MessagePack row).</b> The number is the one
/// the reader uses for its own end-token decision: an array level counts <b>elements</b>, a map level
/// counts <b>key-value pairs</b> (MessagePack stores <c>2n</c> elements for <c>n</c> pairs, so the reader
/// halves its own counter — spec RK-3 exists precisely to stop the divide from being forgotten). It is a
/// query, not a read: it never moves the position, never changes
/// <see cref="ITokenReader.TokenType"/>/<see cref="ITokenReader.Depth"/>/
/// <see cref="ITokenReader.NeedsMoreData"/> and never invalidates the value span.
/// </para>
/// <para>
/// <b>Only a collection start token answers (spec §3.1: "no collection token was produced yet" ⇒
/// <c>false</c>; spec §3.3: "<c>_depth == 0</c> 或当前 token 不是 Start ⇒ <c>false</c>").</b> That is also
/// why the mid-map "odd <c>_elementStack</c>" state is not a queryable state: right after a start token
/// the level that <c>_depth - 1</c> names has <i>just</i> been pushed (an array with <c>n</c>, a map with
/// <c>2n</c>), so the halving never sees an odd number. What RK-3 really asks for — "count the pairs, not
/// the tokens" — is covered instead by map payloads with an <b>odd pair count</b> (3, 15, 255, 65535):
/// reporting <c>2n</c> or forgetting the divide shows up there.
/// </para>
/// <para>
/// <b>Both tokenizers.</b> <see cref="MsgPackReader"/> has two separate <c>Read</c> implementations
/// (<c>ReadSpan</c> / <c>ReadSequential</c>), so every assertion runs in span mode and in sequence mode.
/// Reader work happens in the synchronous helpers below: a <see cref="ref struct"/> cannot live across an
/// <c>await</c>.
/// </para>
/// </summary>
public class MsgPackCollectionCountTests
{
    /// <summary>
    /// Counts that matter: 0 and 1 (empty / single), 15 and 16 (fixarray↔array16 boundary), 255 and 256
    /// (array16 high end / the first count that needs array32), 65535 and 65536 (array32 with a 4-byte
    /// header). Plus 3, which is the smallest odd <i>pair</i> count for maps (spec RK-3).
    /// </summary>
    public static IEnumerable<(string Kind, int Count)> CollectionCases()
    {
        yield return ("fixarray", 0);
        yield return ("fixarray", 1);
        yield return ("fixarray", 3);
        yield return ("fixarray", 15);
        yield return ("array16", 16);
        yield return ("array16", 255);
        yield return ("array16", 256);
        yield return ("array32", 65535);
        yield return ("array32", 65536);
        yield return ("fixmap", 0);
        yield return ("fixmap", 1);
        yield return ("fixmap", 3);
        yield return ("fixmap", 15);
        yield return ("map16", 16);
        yield return ("map16", 255);
        yield return ("map16", 256);
        yield return ("map32", 65535);
        yield return ("map32", 65536);
    }

    /// <summary>Payloads whose single token is not a collection start, by name (data sources pass names).</summary>
    private static readonly (string Name, byte[] Payload)[] Scalars =
    [
        ("nil", new byte[] { 0xC0 }),
        ("bool-true", new byte[] { 0xC3 }),
        ("fixint-1", new byte[] { 0x01 }),
        ("negfixint-minus1", new byte[] { 0xFF }),
        ("uint64-max", new byte[] { 0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }),
        ("float64-1.5", new byte[] { 0xCB, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }),
        ("fixstr-a", new byte[] { 0xA1, (byte)'a' }),
        ("str8-empty", new byte[] { 0xD9, 0x00 }),
        ("bin8-3", new byte[] { 0xC4, 0x03, 0x01, 0x02, 0x03 }),
        ("fixext1", new byte[] { 0xD4, 0x07, 0x42 }),
    ];

    public static IEnumerable<string> ScalarNames() => Scalars.Select(s => s.Name);

    /// <summary>The capability is declared, not inferred (spec §3.1, first member).</summary>
    [Test]
    public async Task HasCountedCollections_IsTrue()
    {
        await Assert
            .That(DeclaresCountedCollections())
            .IsTrue()
            .Because("MessagePack carries the element count in every array/map header");
    }

    /// <summary>
    /// Every header width reports the declared count at its start token, twice in a row, without moving:
    /// <c>BytesConsumed</c>, <c>TokenType</c>, <c>Depth</c> and <c>NeedsMoreData</c> are identical before
    /// and after both queries (spec §3.1 "Never changes observable state").
    /// </summary>
    [Test]
    [MethodDataSource(nameof(CollectionCases))]
    public async Task StartToken_ReportsTheDeclaredCount_WithoutAdvancing(string kind, int count)
    {
        var payload = Payload(kind, count);
        var form = kind.Contains("map", StringComparison.Ordinal)
            ? TokenType.ObjectStart
            : TokenType.ArrayStart;

        foreach (var sequence in new[] { false, true })
        {
            var probe = ProbeStart(payload, sequence);
            var label = $"{kind}-{count} (sequence={sequence})";

            await Assert.That(probe.DeclaresCounts).IsTrue().Because(label);
            await Assert.That(probe.Read).IsTrue().Because(label);
            await Assert
                .That(probe.Form)
                .IsEqualTo(form)
                .Because($"{label}: the payload must open a collection");

            await Assert
                .That(probe.CountOk)
                .IsTrue()
                .Because($"{label}: a start token of a counted format must answer");
            await Assert
                .That(probe.Count)
                .IsEqualTo(count)
                .Because($"{label}: {kind} declares {count} element iteration(s)");
            await Assert
                .That(probe.SecondCountOk)
                .IsTrue()
                .Because($"{label}: the query is idempotent");
            await Assert.That(probe.SecondCount).IsEqualTo(count).Because(label);

            await Assert
                .That(probe.ConsumedAfterQuery)
                .IsEqualTo(probe.Consumed)
                .Because($"{label}: TryGetCollectionCount must not advance");
            await Assert
                .That(probe.ConsumedAfterSecondQuery)
                .IsEqualTo(probe.Consumed)
                .Because($"{label}: two queries must not advance either");
            await Assert
                .That(probe.TokenTypeAfterQuery)
                .IsEqualTo(probe.Form)
                .Because($"{label}: the query must not change TokenType");
            await Assert.That(probe.DepthAfterQuery).IsEqualTo(probe.Depth).Because(label);
            await Assert
                .That(probe.NeedsMoreDataAfterQuery)
                .IsEqualTo(probe.NeedsMoreData)
                .Because($"{label}: the query must not change NeedsMoreData");
        }
    }

    /// <summary>
    /// An empty collection answers <c>true</c> + <c>0</c> — not "unknown". A capability that only reported
    /// non-empty collections would send the generated loop down the <c>Read</c>-driven fallback for the
    /// common "no items" case (spec §3.1: the answer must be <c>true</c> after <i>every</i> start token).
    /// </summary>
    [Test]
    public async Task EmptyCollection_AnswersTrueWithZero()
    {
        foreach (
            var (name, payload) in new (string, byte[])[]
            {
                ("fixarray-empty", new byte[] { 0x90 }),
                ("fixmap-empty", new byte[] { 0x80 }),
                ("array16-empty", new byte[] { 0xDC, 0x00, 0x00 }),
                ("map16-empty", new byte[] { 0xDE, 0x00, 0x00 }),
                ("array32-empty", new byte[] { 0xDD, 0x00, 0x00, 0x00, 0x00 }),
                ("map32-empty", new byte[] { 0xDF, 0x00, 0x00, 0x00, 0x00 }),
            }
        )
        {
            var probe = ProbeStart(payload, sequence: false);
            await Assert.That(probe.CountOk).IsTrue().Because(name);
            await Assert.That(probe.Count).IsEqualTo(0).Because(name);
            await Assert.That(probe.SecondCountOk).IsTrue().Because(name);
            await Assert.That(probe.SecondCount).IsEqualTo(0).Because(name);
        }
    }

    /// <summary>No count is reported where there is no collection: scalars, and before the first read.</summary>
    [Test]
    [MethodDataSource(nameof(ScalarNames))]
    public async Task NonCollectionToken_AnswersFalseWithZero(string name)
    {
        var payload = Scalars.First(s => s.Name == name).Payload;
        foreach (var sequence in new[] { false, true })
        {
            var probe = ProbeScalar(payload, sequence);
            var label = $"{name} (sequence={sequence})";

            await Assert.That(probe.DeclaresCounts).IsTrue().Because(label);
            await Assert.That(probe.Read).IsTrue().Because(label);
            await Assert
                .That(probe.Form)
                .IsNotEqualTo(TokenType.ArrayStart)
                .Because($"{label}: the corpus entry must not be a collection start");
            await Assert.That(probe.Form).IsNotEqualTo(TokenType.ObjectStart).Because(label);
            await Assert
                .That(probe.CountOk)
                .IsFalse()
                .Because($"{label}: a scalar carries no collection count");
            await Assert
                .That(probe.Count)
                .IsEqualTo(0)
                .Because($"{label}: a failed query must zero the count (spec §3.1)");
        }
    }

    /// <summary>Before the first <c>read</c> there is no collection token to describe (spec §3.1).</summary>
    [Test]
    public async Task BeforeFirstRead_AnswersFalseWithZero()
    {
        foreach (var sequence in new[] { false, true })
        {
            var probe = ProbeBeforeFirstRead(sequence);
            await Assert.That(probe.CountOk).IsFalse().Because($"sequence={sequence}");
            await Assert.That(probe.Count).IsEqualTo(0).Because($"sequence={sequence}");
        }
    }

    /// <summary>
    /// The end token and everything after it: the level has been popped, so there is nothing to count
    /// (spec §3.3: <c>_depth == 0</c> or a non-start token ⇒ <c>false</c>).
    /// </summary>
    [Test]
    public async Task EndTokenAndAfter_AnswersFalseWithZero()
    {
        foreach (var sequence in new[] { false, true })
        {
            var probe = ProbePastTheEnd(new byte[] { 0x91, 0x01 }, sequence);
            await Assert.That(probe.Read).IsTrue().Because($"sequence={sequence}");
            await Assert
                .That(probe.Form)
                .IsEqualTo(TokenType.ArrayEnd)
                .Because($"sequence={sequence}: the last token must be the end token");
            await Assert.That(probe.Depth).IsEqualTo(0).Because($"sequence={sequence}");
            await Assert.That(probe.CountOk).IsFalse().Because($"sequence={sequence}");
            await Assert.That(probe.Count).IsEqualTo(0).Because($"sequence={sequence}");
        }
    }

    /// <summary>
    /// A collection the wire promises more of than it delivers: querying it is a query, not a read, so it
    /// must not throw — the answer is the number the reader's own end-token decision is based on, i.e. the
    /// declared count minus what has been consumed (spec §3.1, plan T5 acceptance 3).
    /// </summary>
    [Test]
    public async Task TruncatedCollection_DoesNotThrow_AndReportsTheDeclaredCount()
    {
        foreach (var sequence in new[] { false, true })
        {
            // fixarray(3) with two elements and then the end of the input.
            var probe = ProbeStart(new byte[] { 0x93, 0x01, 0x02 }, sequence);
            await Assert.That(probe.CountOk).IsTrue().Because($"sequence={sequence}");
            await Assert.That(probe.Count).IsEqualTo(3).Because($"sequence={sequence}");

            // map(2 pairs) with one and a half pairs present.
            var map = ProbeStart(new byte[] { 0x82, 0x01, 0x02, 0x03 }, sequence);
            await Assert.That(map.CountOk).IsTrue().Because($"sequence={sequence}");
            await Assert.That(map.Count).IsEqualTo(2).Because($"sequence={sequence}");
        }
    }

    /// <summary>
    /// A map level counts <b>pairs</b>, not tokens (spec §3.1 contract, §3.3 MessagePack row, RK-3).
    /// The corpus deliberately includes odd pair counts: reporting <c>2n</c> (the raw element counter)
    /// fails here, which is exactly the mistake RK-3 names.
    /// </summary>
    [Test]
    public async Task MapLevel_CountsPairs_NotTokens()
    {
        foreach (var pairs in new[] { 1, 2, 3, 15, 16, 255 })
        {
            var payload = pairs <= 15 ? Payload("fixmap", pairs) : Payload("map16", pairs);
            var probe = ProbeStart(payload, sequence: false);
            await Assert
                .That(probe.Count)
                .IsEqualTo(pairs)
                .Because($"a map of {pairs} pair(s) yields {pairs} iteration(s), not {pairs * 2}");
        }
    }

    /// <summary>
    /// Mid-collection the current token is not a start token, so there is no count to report — this is the
    /// "key position / value position" probe, and the only answer the contract allows there is
    /// <c>false</c> (spec §3.1: "no collection token was produced yet"; §3.3: "当前 token 不是 Start
    /// ⇒ <c>false</c>"). Documented here because plan T5 acceptance item 2 reads as if a map reported a
    /// live remaining-pair count at every position; the spec it cites does not say that, and a consumer
    /// only ever asks right after the start token.
    /// </summary>
    [Test]
    public async Task MidCollection_AnswersFalse()
    {
        foreach (var sequence in new[] { false, true })
        {
            var atKey = ProbeMidMap(sequence, keyPosition: true);
            await Assert.That(atKey).IsEqualTo("false/0").Because($"sequence={sequence}");
            var atValue = ProbeMidMap(sequence, keyPosition: false);
            await Assert.That(atValue).IsEqualTo("false/0").Because($"sequence={sequence}");
        }
    }

    // ── readers under test ──

    private static bool DeclaresCountedCollections() =>
        new MsgPackReader(new byte[] { 0x90 }).HasCountedCollections;

    private readonly record struct StartProbe(
        bool DeclaresCounts,
        bool Read,
        TokenType Form,
        int Depth,
        bool NeedsMoreData,
        long Consumed,
        bool CountOk,
        int Count,
        bool SecondCountOk,
        int SecondCount,
        long ConsumedAfterQuery,
        long ConsumedAfterSecondQuery,
        TokenType TokenTypeAfterQuery,
        int DepthAfterQuery,
        bool NeedsMoreDataAfterQuery
    );

    private static StartProbe ProbeStart(byte[] payload, bool sequence)
    {
        var reader = Open(payload, sequence);
        bool read = reader.Read();
        var form = reader.TokenType;
        int depth = reader.Depth;
        bool needsMoreData = reader.NeedsMoreData;
        long consumed = reader.BytesConsumed;

        bool countOk = reader.TryGetCollectionCount(out int count);
        long consumedAfter = reader.BytesConsumed;
        bool secondOk = reader.TryGetCollectionCount(out int second);
        long consumedAfterSecond = reader.BytesConsumed;

        return new StartProbe(
            reader.HasCountedCollections,
            read,
            form,
            depth,
            needsMoreData,
            consumed,
            countOk,
            count,
            secondOk,
            second,
            consumedAfter,
            consumedAfterSecond,
            reader.TokenType,
            reader.Depth,
            reader.NeedsMoreData
        );
    }

    private readonly record struct ScalarProbe(
        bool DeclaresCounts,
        bool Read,
        TokenType Form,
        bool CountOk,
        int Count
    );

    private static ScalarProbe ProbeScalar(byte[] payload, bool sequence)
    {
        var reader = Open(payload, sequence);
        bool read = reader.Read();
        bool ok = reader.TryGetCollectionCount(out int count);
        return new ScalarProbe(reader.HasCountedCollections, read, reader.TokenType, ok, count);
    }

    private static ScalarProbe ProbeBeforeFirstRead(bool sequence)
    {
        var reader = Open(new byte[] { 0x90 }, sequence);
        bool ok = reader.TryGetCollectionCount(out int count);
        return new ScalarProbe(reader.HasCountedCollections, false, reader.TokenType, ok, count);
    }

    private readonly record struct EndProbe(
        bool Read,
        TokenType Form,
        int Depth,
        bool CountOk,
        int Count
    );

    private static EndProbe ProbePastTheEnd(byte[] payload, bool sequence)
    {
        var reader = Open(payload, sequence);
        while (reader.Read()) { }

        bool ok = reader.TryGetCollectionCount(out int count);
        return new EndProbe(true, reader.TokenType, reader.Depth, ok, count);
    }

    /// <summary>Runs a fixmap(2) up to its first key (or its first value) and reports the query's answer.</summary>
    private static string ProbeMidMap(bool sequence, bool keyPosition)
    {
        var reader = Open(new byte[] { 0x82, 0x01, 0x02, 0x03, 0x04 }, sequence);
        reader.Read(); // ObjectStart
        reader.Read(); // first key
        if (!keyPosition)
            reader.Read(); // first value
        return reader.TryGetCollectionCount(out int count)
            ? "true/" + count.ToString(CultureInfo.InvariantCulture)
            : "false/0";
    }

    private static MsgPackReader Open(byte[] payload, bool sequence) =>
        sequence
            ? new MsgPackReader(new ReadOnlySequence<byte>(payload))
            : new MsgPackReader(payload);

    // ── payload builders (independent of the reader under test) ──

    /// <summary>
    /// One collection with an exact header: fixarray/fixmap (1-byte header), array16/map16 (3 bytes),
    /// array32/map32 (5 bytes). Elements are fixints; a map gets two per pair, which is what makes the
    /// "pairs, not tokens" assertion meaningful.
    /// </summary>
    private static byte[] Payload(string kind, int count)
    {
        var bytes = new List<byte>(8 + count * 2);
        switch (kind)
        {
            case "fixarray":
            case "fixmap":
                bytes.Add((byte)((kind == "fixmap" ? 0x80 : 0x90) | count));
                break;
            case "array16":
            case "map16":
                bytes.Add(kind == "map16" ? (byte)0xDE : (byte)0xDC);
                AddBigEndian(bytes, count, 2);
                break;
            case "array32":
            case "map32":
                bytes.Add(kind == "map32" ? (byte)0xDF : (byte)0xDD);
                AddBigEndian(bytes, count, 4);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown header shape");
        }

        var elements = kind.Contains("map", StringComparison.Ordinal) ? count * 2 : count;
        for (int i = 0; i < elements; i++)
            bytes.Add(0x01); // positive fixint 1: a scalar element for both the key and the value slot
        return bytes.ToArray();
    }

    private static void AddBigEndian(List<byte> into, int value, int width)
    {
        for (int shift = (width - 1) * 8; shift >= 0; shift -= 8)
            into.Add((byte)((value >> shift) & 0xFF));
    }
}
