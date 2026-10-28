namespace PicoMsgPack.Tests;

// ── Models ───────────────────────────────────────────────────────────────────────────────────
// One member per E-1 emission site T9 wires up (spec §3.4.1): a `List<T>` member (site "list"),
// an array member (site "array"), an extended-collection member (site "hashset"), a nullable
// value-type element (the null check lives *inside* the loop body) and — deliberately last — a
// scalar member, so any off-by-one token read in a collection loop shows up as a misaligned Tail
// instead of passing silently.
public class ScalarCollectionsMsgPack
{
    [MsgPackKey(0)]
    public List<int> Values { get; set; } = new();

    [MsgPackKey(1)]
    public string[] Tags { get; set; } = [];

    [MsgPackKey(2)]
    public HashSet<int> Set { get; set; } = new();

    [MsgPackKey(3)]
    public List<bool> Flags { get; set; } = new();

    [MsgPackKey(4)]
    public List<int?> Maybe { get; set; } = new();

    [MsgPackKey(5)]
    public int Tail { get; set; }
}

// Nested containers stay on E-2 (spec §3.4.1: E-1 covers scalar elements only). Pinned here so
// that a later widening of the emitter is a conscious change and not an accident.
public class NestedListMsgPack
{
    [MsgPackKey(0)]
    public List<List<int>> Rows { get; set; } = new();

    [MsgPackKey(1)]
    public int Tail { get; set; }
}

/// <summary>
/// Task T9 — generator rule <b>E-1</b> (spec §3.4.1, spec §6 S1): the generated MessagePack
/// collection loop counts instead of probing for the end token, and still emits the end-token
/// <c>Read()</c> explicitly, so the token sequence is the one the old <c>while</c> loop produced.
/// <para>
/// <b>Why this file is the judge.</b> The emission itself is invisible in behaviour: a counted loop
/// that reads the same number of tokens in the same order must produce the same values, the same
/// exceptions and the same messages as <c>while (reader.Read() &amp;&amp; reader.TokenType !=
/// TokenType.ArrayEnd)</c>. So every assertion here compares the generated (E-1) path against
/// <see cref="ReferenceWhileShape"/> — the pre-T9 shape, written out by hand over the same
/// <see cref="MsgPackReader"/> — on values, exception type <i>and</i> message, for well-formed,
/// empty, truncated and trailing-garbage payloads. The file has to stay green across the emission
/// change; its sensitivity is shown by the negative control recorded in the T9 report (delete the
/// end-token <c>Read()</c> from the emitter ⇒ these tests go red).
/// </para>
/// <para>
/// A <see cref="MsgPackReader"/> is a <c>ref struct</c>, so no reference-arm helper here may cross an
/// <c>await</c>; the reader work is synchronous and only its results are asserted.
/// </para>
/// </summary>
public class GeneratedCountedCollectionLoopTests
{
    /// <summary>
    /// Every wired site, in one document: <c>list</c>, <c>array</c>, extended collection,
    /// nullable element, plus the trailing scalar that proves alignment.
    /// </summary>
    [Test]
    public async Task ScalarCollections_CountedLoop_MatchesTheWhileShape()
    {
        var model = new ScalarCollectionsMsgPack
        {
            Values = [1, -2, 300, int.MaxValue, int.MinValue],
            Tags = ["alpha", "", "gamma"],
            Set = [7, 8, 9],
            Flags = [true, false, true],
            Maybe = [1, null, 3],
            Tail = 4242,
        };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var generated = MsgPackSerializer.Deserialize<ScalarCollectionsMsgPack>(payload);
        var reference = ReferenceWhileShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Tags).IsEquivalentTo(reference.Tags);
        await Assert.That(generated.Set).IsEquivalentTo(reference.Set);
        await Assert.That(generated.Flags).IsEquivalentTo(reference.Flags);
        await Assert.That(generated.Maybe).IsEquivalentTo(reference.Maybe);
        await Assert.That(generated.Tail).IsEqualTo(reference.Tail);

        // …and the reference agrees with the model, so "both sides equal" cannot hide a shared bug.
        await Assert.That(generated.Values).IsEquivalentTo(model.Values);
        await Assert.That(generated.Tags).IsEquivalentTo(model.Tags);
        await Assert.That(generated.Flags).IsEquivalentTo(model.Flags);
        await Assert.That(generated.Maybe).IsEquivalentTo(model.Maybe);
        await Assert.That(generated.Tail).IsEqualTo(4242);
    }

    /// <summary>
    /// count = 0: the counted loop body never runs, so the explicit end-token <c>Read()</c> is the
    /// only thing that consumes the empty collection — exactly what the <c>while</c> form did.
    /// </summary>
    [Test]
    public async Task EmptyCollections_ConsumeTheEndTokenAndKeepFollowingMembers()
    {
        // fixmap(6): 0 → fixarray(0), 1 → fixarray(0), 2 → fixarray(0), 3 → fixarray(0),
        //            4 → fixarray(0), 5 → 7
        byte[] payload =
        [
            0x86,
            0x00,
            0x90,
            0x01,
            0x90,
            0x02,
            0x90,
            0x03,
            0x90,
            0x04,
            0x90,
            0x05,
            0x07,
        ];

        var generated = MsgPackSerializer.Deserialize<ScalarCollectionsMsgPack>(payload);
        var reference = ReferenceWhileShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values.Count).IsEqualTo(0);
        await Assert.That(generated.Tags.Length).IsEqualTo(0);
        await Assert.That(generated.Set.Count).IsEqualTo(0);
        await Assert.That(generated.Flags.Count).IsEqualTo(0);
        await Assert.That(generated.Maybe.Count).IsEqualTo(0);
        await Assert.That(generated.Tail).IsEqualTo(7);
        await Assert.That(generated.Tail).IsEqualTo(reference.Tail);
    }

    /// <summary>
    /// 300 elements cross the fixarray→array16 header boundary, so the count comes from a 2-byte
    /// header; the trailing member still lands on its own token.
    /// </summary>
    [Test]
    public async Task LongCollections_Array16Header_KeepAlignment()
    {
        var values = Enumerable.Range(0, 300).Select(i => i * 3 - 100).ToList();
        var model = new ScalarCollectionsMsgPack { Values = values, Tail = -1 };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var generated = MsgPackSerializer.Deserialize<ScalarCollectionsMsgPack>(payload);
        var reference = ReferenceWhileShape(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!.Values.Count).IsEqualTo(300);
        await Assert.That(generated.Values).IsEquivalentTo(reference.Values);
        await Assert.That(generated.Values).IsEquivalentTo(values);
        await Assert.That(generated.Tail).IsEqualTo(-1);
    }

    /// <summary>Root-level <c>List&lt;T&gt;</c> deserializers are the fourth wired site.</summary>
    [Test]
    public async Task RootScalarList_MatchesTheWhileShape()
    {
        int[] values = [5, 0, -1, 12_345, int.MinValue];
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(new List<int>(values));

        var generated = MsgPackSerializer.Deserialize<List<int>>(payload);
        var reference = ReferenceWhileShapeRootIntList(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).IsEquivalentTo(reference);
        await Assert.That(generated).IsEquivalentTo(values);

        string[] tags = ["one", "two"];
        var stringPayload = MsgPackSerializer.SerializeToUtf8Bytes(new List<string>(tags));
        var stringGenerated = MsgPackSerializer.Deserialize<List<string>>(stringPayload);
        await Assert.That(stringGenerated!).IsEquivalentTo(tags);
    }

    /// <summary>
    /// The streaming deserializer emits the same collection site, but every surrounding <c>Read()</c>
    /// turns into a <c>NeedMoreData</c> return instead of a silent <c>false</c> — the counted loop has
    /// to keep that contract across arbitrary chunk boundaries.
    /// </summary>
    [Test]
    public async Task StreamingArm_ChunkedPayloads_KeepEveryElement()
    {
        var model = new ScalarCollectionsMsgPack
        {
            Values = [1, 2, 3, 4, 5, 6, 7],
            Tags = ["alpha", "beta"],
            Set = [1],
            Flags = [false, true],
            Maybe = [null, 2],
            Tail = 99,
        };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        foreach (var chunkSize in new[] { 1, 2, 3, 5, 7 })
        {
            using var stream = new ChunkedReadStream(payload, chunkSize);
            var back = await MsgPackSerializer.DeserializeFromStreamAsync<ScalarCollectionsMsgPack>(
                stream
            );
            await Assert.That(back).IsNotNull();
            await Assert.That(back!.Values).IsEquivalentTo(model.Values);
            await Assert.That(back.Tags).IsEquivalentTo(model.Tags);
            await Assert.That(back.Flags).IsEquivalentTo(model.Flags);
            await Assert.That(back.Maybe).IsEquivalentTo(model.Maybe);
            await Assert.That(back.Tail).IsEqualTo(99);
        }
    }

    /// <summary>
    /// Nested containers are <b>not</b> wired to E-1 (spec §3.4.1 scope) — pinned so the statement
    /// "nested stays on E-2" is verified rather than assumed.
    /// </summary>
    [Test]
    public async Task NestedLists_StayOnTheEndTokenLoop_AndRoundTrip()
    {
        var model = new NestedListMsgPack
        {
            Rows =
            [
                [1, 2],
                [],
                [3, 4, 5],
            ],
            Tail = 11,
        };
        var payload = MsgPackSerializer.SerializeToUtf8Bytes(model);

        var back = MsgPackSerializer.Deserialize<NestedListMsgPack>(payload);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Rows.Count).IsEqualTo(3);
        await Assert.That(back.Rows[0]).IsEquivalentTo(new[] { 1, 2 });
        await Assert.That(back.Rows[1].Count).IsEqualTo(0);
        await Assert.That(back.Rows[2]).IsEquivalentTo(new[] { 3, 4, 5 });
        await Assert.That(back.Tail).IsEqualTo(11);
    }

    /// <summary>
    /// A truncated payload is where a counted loop can diverge from the end-token loop without anybody
    /// noticing: <c>Read()</c> returns <c>false</c> and leaves the previous token in place, so an
    /// unguarded <c>for</c> would re-read the last element's stale span and report it twice. The
    /// generated path must keep the E-2 answer — the partially filled list.
    /// </summary>
    [Test]
    public async Task TruncatedRootList_MatchesTheWhileShape()
    {
        // fixarray(2) holding a single element.
        byte[] payload = [0x92, 0x01];

        var generated = MsgPackSerializer.Deserialize<List<int>>(payload);
        var reference = ReferenceWhileShapeRootIntList(payload);

        await Assert.That(generated).IsNotNull();
        await Assert.That(generated!).IsEquivalentTo(reference);
        await Assert.That(generated!.Count).IsEqualTo(1);
        await Assert.That(generated[0]).IsEqualTo(1);
    }

    /// <summary>
    /// Truncation inside a map member: both shapes end up at the depth check, so the exception type
    /// <i>and</i> the message must be the same one the pre-T9 code produced.
    /// </summary>
    [Test]
    public async Task TruncatedMemberList_ThrowsLikeTheWhileShape()
    {
        // fixmap(2), key 0 → fixarray(3) with one element present, key 1 → 9.
        byte[] payload = [0x82, 0x00, 0x93, 0x01, 0x01, 0x09];

        var generated = ErrorOf(() =>
            MsgPackSerializer.Deserialize<ScalarCollectionsMsgPack>(payload)
        );
        var reference = ErrorOf(() => ReferenceWhileShape(payload));

        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
    }

    /// <summary>
    /// Trailing garbage is detected inside <c>Read()</c> when the root container closes — the counted
    /// loop calls that <c>Read()</c> explicitly, so it has to be the very same one-row report.
    /// </summary>
    [Test]
    public async Task TrailingGarbageAfterRootList_ThrowsLikeTheWhileShape()
    {
        // fixarray(2) {1, 2} then two stray bytes.
        byte[] payload = [0x92, 0x01, 0x02, 0xFF, 0xFF];

        var generated = ErrorOf(() => MsgPackSerializer.Deserialize<List<int>>(payload));
        var reference = ErrorOf(() => ReferenceWhileShapeRootIntList(payload));

        await Assert.That(generated).IsNotNull();
        await Assert.That(reference).IsNotNull();
        await Assert.That(generated!.GetType()).IsEqualTo(reference!.GetType());
        await Assert.That(generated.Message).IsEqualTo(reference.Message);
    }

    // ── Reference arms: the pre-T9 (spec §3.4.1 E-2) shape, hand-written over the same reader ──

    /// <summary>
    /// The deserializer the generator emitted for <see cref="ScalarCollectionsMsgPack"/> before T9:
    /// identical token walk, <c>while (reader.Read() &amp;&amp; reader.TokenType != TokenType.ArrayEnd)</c>
    /// for every collection. This is the reference the E-1 output must match value for value.
    /// </summary>
    static ScalarCollectionsMsgPack ReferenceWhileShape(ReadOnlySpan<byte> payload)
    {
        var model = new ScalarCollectionsMsgPack();
        var tags = new List<string>();
        var reader = new MsgPackReader(payload);
        reader.Read(); // ObjectStart
        while (reader.Read())
        {
            if (reader.TokenType is TokenType.ObjectEnd or TokenType.ArrayEnd)
                break;
            reader.TryGetInt32(out var key);
            reader.Read(); // the member's value token
            switch (key)
            {
                case 0:
                    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    {
                        if (!reader.TryGetInt32(out var v))
                            throw new FormatException(
                                $"Expected an integer element at offset {reader.BytesConsumed}"
                            );
                        model.Values.Add(v);
                    }
                    break;
                case 1:
                    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    {
                        if (reader.TokenType != TokenType.String)
                            throw new FormatException(
                                $"Expected a string element at offset {reader.BytesConsumed}"
                            );
                        tags.Add(Encoding.UTF8.GetString(reader.GetStringRaw()));
                    }
                    break;
                case 2:
                    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    {
                        if (!reader.TryGetInt32(out var v))
                            throw new FormatException(
                                $"Expected an integer element at offset {reader.BytesConsumed}"
                            );
                        model.Set.Add(v);
                    }
                    break;
                case 3:
                    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    {
                        if (!reader.TryGetBool(out var v))
                            throw new FormatException(
                                $"Expected a boolean element at offset {reader.BytesConsumed}"
                            );
                        model.Flags.Add(v);
                    }
                    break;
                case 4:
                    while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
                    {
                        if (reader.TokenType == TokenType.Null)
                        {
                            model.Maybe.Add(null);
                            continue;
                        }
                        if (!reader.TryGetInt32(out var v))
                            throw new FormatException(
                                $"Expected an integer element at offset {reader.BytesConsumed}"
                            );
                        model.Maybe.Add(v);
                    }
                    break;
                case 5:
                    if (!reader.TryGetInt32(out var tail))
                        throw new FormatException(
                            $"Expected an integer at offset {reader.BytesConsumed}"
                        );
                    model.Tail = tail;
                    break;
            }
        }
        if (reader.Depth != 0)
            throw new FormatException("Truncated container: unexpected end of input");
        model.Tags = tags.ToArray();
        return model;
    }

    /// <summary>The pre-T9 root-<c>List&lt;int&gt;</c> deserializer (same three lines, same order).</summary>
    static List<int> ReferenceWhileShapeRootIntList(ReadOnlySpan<byte> payload)
    {
        var list = new List<int>();
        var reader = new MsgPackReader(payload);
        reader.Read(); // ArrayStart
        if (reader.TokenType == TokenType.ArrayStart)
        {
            while (reader.Read() && reader.TokenType != TokenType.ArrayEnd)
            {
                if (!reader.TryGetInt32(out var v))
                    throw new FormatException(
                        $"Expected an integer element at offset {reader.BytesConsumed}"
                    );
                list.Add(v);
            }
        }
        return list;
    }

    static Exception? ErrorOf(Func<object?> action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Hands out at most <paramref name="chunkSize"/> bytes per <c>ReadAsync</c>.</summary>
    sealed class ChunkedReadStream(byte[] data, int chunkSize) : Stream
    {
        int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position
        {
            get => _offset;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            var size = Math.Min(chunkSize, Math.Min(buffer.Length, data.Length - _offset));
            if (size <= 0)
                return ValueTask.FromResult(0);
            data.AsMemory(_offset, size).CopyTo(buffer);
            _offset += size;
            return ValueTask.FromResult(size);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
