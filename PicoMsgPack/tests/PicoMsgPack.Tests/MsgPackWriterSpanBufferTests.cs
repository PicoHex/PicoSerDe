namespace PicoMsgPack.Tests;

/// <summary>
/// Task 4b guard rails. <see cref="MsgPackWriter"/> no longer commits every token to the
/// <see cref="IBufferWriter{T}"/>; it writes into a span of its own and touches the buffer
/// only when that span runs out (<c>Ensure</c>) or when the caller says the value is
/// finished (<c>FlushToBuffer</c>, the <c>Buffer</c> hand-off, or closing the outermost
/// container). Two failure modes are invisible to a round-trip test — a chunk that is
/// never committed because a flush was forgotten, and a chunk committed twice — so every
/// test here compares <em>bytes</em>, against goldens recorded from the pre-span-cache
/// implementation (HEAD before this change; raw capture in
/// <c>.superpowers/sdd/perf-mp-span/logs/golden-old-HEAD.txt</c>).
/// </summary>
public class MsgPackWriterSpanBufferTests
{
    // ── Goldens: output of the previous (commit-per-token) writer ───────────
    private const string EmptyHex = "80";

    private const string TinyHex = "8200A1780101";

    private const string BoundaryHex =
        "8500BF6161616161616161616161616161616161616161616161616161616161616101D9206262626262626262626262"
        + "62626262626262626262626262626262626262626202D9FF636363636363636363636363636363636363636363636363"
        + "636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363"
        + "636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363"
        + "636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363"
        + "636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363636363"
        + "63636363636363636363636363636363636363636363636363636363636363636363636363636303DA01006464646464"
        + "646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464"
        + "646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464"
        + "646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464"
        + "646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464"
        + "646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464646464"
        + "646464646464646464646404DA012C656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565656565"
        + "656565656565656565656565656565656565656565656565656565";

    private const string WideHex =
        "DE001900D920616161616161616161616161616161616161616161616161616161616161616101D92061616161616161"
        + "6161616161616161616161616161616161616161616161616102D9206161616161616161616161616161616161616161"
        + "61616161616161616161616103D920616161616161616161616161616161616161616161616161616161616161616104"
        + "D920616161616161616161616161616161616161616161616161616161616161616105D9206161616161616161616161"
        + "61616161616161616161616161616161616161616106D920616161616161616161616161616161616161616161616161"
        + "616161616161616107D920616161616161616161616161616161616161616161616161616161616161616108D9206161"
        + "61616161616161616161616161616161616161616161616161616161616109D920616161616161616161616161616161"
        + "61616161616161616161616161616161610AD92061616161616161616161616161616161616161616161616161616161"
        + "616161610BD92061616161616161616161616161616161616161616161616161616161616161610CD920616161616161"
        + "61616161616161616161616161616161616161616161616161610DD92061616161616161616161616161616161616161"
        + "616161616161616161616161610ED9206161616161616161616161616161616161616161616161616161616161616161"
        + "0FD920616161616161616161616161616161616161616161616161616161616161616110D92061616161616161616161"
        + "6161616161616161616161616161616161616161616111D9206161616161616161616161616161616161616161616161"
        + "61616161616161616112D920616161616161616161616161616161616161616161616161616161616161616113D92061"
        + "6161616161616161616161616161616161616161616161616161616161616114D2000F424015D2FFF0BDC016D3000000"
        + "0218711A0017CB401200000000000018C3";

    private const string NestedHex =
        "87000701A5416C696365028200A8313233204D61696E01A253460392A3646576A562656E63680481A16BA176059301020306C403010203";

    private const string ManualMapHex = "82002A01A26869";

    private const string TwoMapsHex = "810001810102";

    /// <summary>Digest of the 24 981-byte payload — too long to keep as hex.</summary>
    private const string HugeSha256Hex =
        "2F25D868F3B0DFE3785A2B1F4A2F00571EC90ECBDA9FBA5357FB26DEB7F63C0C";

    private const int HugeLength = 24981;

    /// <summary>Slack values tried for the forced-flush writers; 0 means "exactly what was asked".</summary>
    private static readonly int[] Slacks = [0, 1, 2, 3, 7];

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Explains <em>where</em> two streams diverge, so a failure is actionable.</summary>
    private static string Diff(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        int n = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < n; i++)
            if (expected[i] != actual[i])
                return $"byte {i}: expected 0x{expected[i]:X2}, actual 0x{actual[i]:X2} (lengths {expected.Length}/{actual.Length})";
        return expected.Length == actual.Length
            ? "identical"
            : $"prefix identical, lengths {expected.Length}/{actual.Length}";
    }

    private static async Task ExpectBytes(string expectedHex, byte[] actual, string what)
    {
        var expected = Convert.FromHexString(expectedHex);
        await Assert.That(Diff(expected, actual)).IsEqualTo("identical").Because(what);
    }

    // ── 1. The byte stream itself ───────────────────────────────────────────

    [Test]
    public async Task EmptyObject_IsOneFixMapZero()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(new SpanEmptyModel());
        await ExpectBytes(EmptyHex, bytes, "empty object");
        await Assert.That(bytes.Length).IsEqualTo(1);
        await Assert.That(bytes[0]).IsEqualTo((byte)0x80);
    }

    [Test]
    public async Task Serialize_MatchesThePreSpanCacheGolden()
    {
        await ExpectBytes(
            EmptyHex,
            MsgPackSerializer.SerializeToUtf8Bytes(new SpanEmptyModel()),
            "empty"
        );
        await ExpectBytes(
            TinyHex,
            MsgPackSerializer.SerializeToUtf8Bytes(new SpanTinyModel()),
            "tiny"
        );
        await ExpectBytes(
            BoundaryHex,
            MsgPackSerializer.SerializeToUtf8Bytes(new SpanBoundaryModel()),
            "string boundaries (31/32/255/256/300)"
        );
        await ExpectBytes(
            WideHex,
            MsgPackSerializer.SerializeToUtf8Bytes(new SpanWideModel()),
            "wide (737 B — several spans)"
        );
        await ExpectBytes(
            NestedHex,
            MsgPackSerializer.SerializeToUtf8Bytes(SpanNestedModel.Create()),
            "nested object + list + dict + binary"
        );
    }

    [Test]
    public async Task HugePayload_MatchesThePreSpanCacheDigest()
    {
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(SpanHugeModel.Create());
        await Assert.That(bytes.Length).IsEqualTo(HugeLength);
        await Assert
            .That(Hex(System.Security.Cryptography.SHA256.HashData(bytes)))
            .IsEqualTo(HugeSha256Hex);
    }

    /// <summary>
    /// The strongest structural test: a writer whose spans are exactly the size the encoder
    /// asked for (plus <c>slack</c>), so the span is exhausted — and has to be committed —
    /// after almost every token. An <c>Ensure</c> that forgets to flush its predecessor, or
    /// a write that happens before room is asked for, changes the bytes.
    /// </summary>
    [Test]
    public async Task Serialize_IntoExactSizedSpans_IsByteIdentical()
    {
        foreach (var slack in Slacks)
        {
            await ExpectBytes(
                EmptyHex,
                Serialize(new SpanEmptyModel(), slack),
                $"empty slack={slack}"
            );
            await ExpectBytes(
                TinyHex,
                Serialize(new SpanTinyModel(), slack),
                $"tiny slack={slack}"
            );
            await ExpectBytes(
                BoundaryHex,
                Serialize(new SpanBoundaryModel(), slack),
                $"boundary slack={slack}"
            );
            await ExpectBytes(
                WideHex,
                Serialize(new SpanWideModel(), slack),
                $"wide slack={slack}"
            );
            await ExpectBytes(
                NestedHex,
                Serialize(SpanNestedModel.Create(), slack),
                $"nested slack={slack}"
            );

            var huge = Serialize(SpanHugeModel.Create(), slack);
            await Assert.That(huge.Length).IsEqualTo(HugeLength);
            await Assert
                .That(Hex(System.Security.Cryptography.SHA256.HashData(huge)))
                .IsEqualTo(HugeSha256Hex)
                .Because($"huge slack={slack}");
        }
    }

    /// <summary>
    /// Reproduces <c>SerializeToUtf8Bytes</c> but through an exact-span writer, i.e. with a
    /// different flush schedule over the same tokens.
    /// </summary>
    private static byte[] Serialize<T>(T value, int slack)
        where T : allows ref struct
    {
        var writer = new ExactBufferWriter(slack);
        MsgPackSerializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    // ── 2. The flush exits ──────────────────────────────────────────────────

    [Test]
    public async Task ManualWriter_MatchesThePreSpanCacheGolden()
    {
        await ExpectBytes(ManualMapHex, BuildManualMap(), "manual map");
    }

    private static byte[] BuildManualMap()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.WriteInt32(1);
        mw.WriteString("hi"u8);
        mw.WriteEndObject();
        mw.FlushToBuffer();
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Closing the outermost container commits implicitly, so hand-written code that ends a
    /// complete value (the shape the sample and most of the writer tests use) keeps working
    /// without calling <c>FlushToBuffer</c>.
    /// </summary>
    [Test]
    public async Task WriteEndAtDepthZero_CommitsImplicitly()
    {
        await ExpectBytes("810001", BuildClosedObject(), "object closed, no explicit flush");
        await ExpectBytes("920102", BuildClosedArray(), "array closed, no explicit flush");
    }

    private static byte[] BuildClosedObject()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(1);
        mw.WriteInt32(0);
        mw.WriteInt32(1);
        mw.WriteEndObject();
        return buf.WrittenSpan.ToArray();
    }

    private static byte[] BuildClosedArray()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartArray(2);
        mw.WriteInt32(1);
        mw.WriteInt32(2);
        mw.WriteEndArray();
        return buf.WrittenSpan.ToArray();
    }

    /// <summary>Boundary case ⑤: writing after a flush starts over in a fresh span.</summary>
    [Test]
    public async Task FlushToBuffer_ThenWriteAgain_ProducesTheConcatenation()
    {
        var (bytes, afterBoth, bytesWritten, writtenCount) = BuildTwoMaps();

        await ExpectBytes(TwoMapsHex, bytes, "two maps in one buffer");
        await Assert.That(bytesWritten).IsEqualTo(writtenCount);

        // Flushing again with nothing pending must not repeat the last chunk.
        await ExpectBytes(TwoMapsHex, afterBoth, "idempotent flush");
    }

    private static (
        byte[] Bytes,
        byte[] AfterExtraFlush,
        long BytesWritten,
        int WrittenCount
    ) BuildTwoMaps()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(1);
        mw.WriteInt32(0);
        mw.WriteInt32(1);
        mw.WriteEndObject();
        mw.FlushToBuffer();
        mw.WriteStartObject(1);
        mw.WriteInt32(1);
        mw.WriteInt32(2);
        mw.WriteEndObject();
        mw.FlushToBuffer();

        var bytes = buf.WrittenSpan.ToArray();
        long written = mw.BytesWritten;
        int count = buf.WrittenCount;

        mw.FlushToBuffer();
        return (bytes, buf.WrittenSpan.ToArray(), written, count);
    }

    /// <summary>
    /// The <c>Buffer</c> hand-off (the generator uses it for custom and polymorphic
    /// serializers) must publish what is still buffered before anyone writes into the raw
    /// buffer, and must drop its span so the next write asks for a fresh one.
    /// </summary>
    [Test]
    public async Task BufferHandoff_CommitsPendingBytes_AndResumes()
    {
        var (sameBuffer, pendingBefore, pendingBytes, final) = BuildHandoff();

        await Assert.That(sameBuffer).IsTrue();
        await Assert.That(pendingBefore).IsEqualTo(3);
        await Assert.That(pendingBytes).IsEquivalentTo(new byte[] { 0x82, 0x00, 0x01 });
        await ExpectBytes("8200010102", final, "hand-off then resume");
    }

    private static (
        bool SameBuffer,
        int PendingBefore,
        byte[] PendingBytes,
        byte[] Final
    ) BuildHandoff()
    {
        var buf = new ArrayBufferWriter<byte>(256);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(1);

        var raw = mw.Buffer;
        bool same = ReferenceEquals(raw, buf);
        int pending = buf.WrittenCount;
        var pendingBytes = buf.WrittenSpan.ToArray();

        // Another writer over the same buffer — the shape a custom serializer has.
        var nested = new MsgPackWriter(raw);
        nested.WriteInt32(1);
        nested.WriteInt32(2);
        nested.FlushToBuffer();

        mw.WriteEndObject();
        return (same, pending, pendingBytes, buf.WrittenSpan.ToArray());
    }

    // ── 3. Boundaries ───────────────────────────────────────────────────────

    [Test]
    public async Task WideObject_IsMoreThanOneSpan_AndRoundTrips()
    {
        var model = new SpanWideModel();
        var bytes = MsgPackSerializer.SerializeToUtf8Bytes(model);
        await Assert.That(bytes.Length).IsGreaterThan(256 * 2);

        var back = MsgPackSerializer.Deserialize<SpanWideModel>(bytes);
        await Assert.That(back!.S00).IsEqualTo(model.S00);
        await Assert.That(back.S19).IsEqualTo(model.S19);
        await Assert.That(back.N02).IsEqualTo(model.N02);
        await Assert.That(back.D00).IsEqualTo(model.D00);
        await Assert.That(back.B00).IsTrue();
    }

    [Test]
    public async Task HugeArray_CrossesManySpans_AndRoundTrips()
    {
        var model = SpanHugeModel.Create();
        var back = MsgPackSerializer.Deserialize<SpanHugeModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Values.Count).IsEqualTo(model.Values.Count);
        for (int i = 0; i < model.Values.Count; i++)
            if (back.Values[i] != model.Values[i])
            {
                await Assert
                    .That(i)
                    .IsEqualTo(-1)
                    .Because($"value {i} changed across the round trip");
                return;
            }
    }

    [Test]
    public async Task NestedContainers_RoundTrip()
    {
        var model = SpanNestedModel.Create();
        var back = MsgPackSerializer.Deserialize<SpanNestedModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Address.City).IsEqualTo(model.Address.City);
        await Assert.That(back.Tags).IsEquivalentTo(model.Tags);
        await Assert.That(back.Meta["k"]).IsEqualTo("v");
        await Assert.That(back.Scores).IsEquivalentTo(model.Scores);
        await Assert.That(back.Payload).IsEquivalentTo(model.Payload);
    }

    [Test]
    public async Task BytesWritten_MatchesTheCommittedLength()
    {
        foreach (var slack in Slacks)
        {
            var (bytesWritten, writtenCount) = BuildArray(slack);
            await Assert.That(bytesWritten).IsEqualTo(writtenCount).Because($"slack={slack}");
        }
    }

    private static (long BytesWritten, int WrittenCount) BuildArray(int slack)
    {
        var writer = new ExactBufferWriter(slack);
        var mw = new MsgPackWriter(writer);
        mw.WriteStartArray(2);
        mw.WriteString("hello"u8);
        mw.WriteInt64(9_000_000_000L);
        mw.WriteEndArray();
        mw.FlushToBuffer();
        return (mw.BytesWritten, writer.WrittenCount);
    }

    /// <summary>
    /// <see cref="IBufferWriter{T}"/> whose <c>GetSpan</c> hands out the requested size (plus
    /// <c>slack</c>) and nothing more, so a write either fits exactly or forces the encoder to
    /// commit first. <c>Advance</c> rejects a count larger than what was handed out, which turns
    /// a cursor bug into a failure instead of silent corruption.
    /// </summary>
    private sealed class ExactBufferWriter : IBufferWriter<byte>
    {
        private readonly int _slack;
        private byte[] _buffer;
        private int _pos;
        private int _handedOut;

        internal ExactBufferWriter(int slack, int capacity = 32)
        {
            _slack = slack;
            _buffer = new byte[Math.Max(capacity, 32)];
        }

        internal int WrittenCount => _pos;

        internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _pos);

        public void Advance(int count)
        {
            if (count < 0 || count > _handedOut)
                throw new InvalidOperationException(
                    $"Advance({count}) with only {_handedOut} bytes handed out"
                );
            _pos += count;
            _handedOut = 0;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            GetSpan(sizeHint);
            return _buffer.AsMemory(_pos, _handedOut);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            int want = sizeHint + _slack;
            if (_pos + want > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_pos + want, _buffer.Length * 2));
            _handedOut = want;
            return _buffer.AsSpan(_pos, want);
        }
    }
}
