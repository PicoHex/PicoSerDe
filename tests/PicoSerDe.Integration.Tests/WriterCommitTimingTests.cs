namespace PicoSerDe.Integration.Tests;

/// <summary>
/// The cross-format contract for <b>when</b> a writer's staged bytes reach the
/// <see cref="IBufferWriter{T}"/> — written once, asserted against both writers.
/// <para>
/// <see cref="JsonWriter"/> and <see cref="MsgPackWriter"/> give <em>two different answers</em>
/// to the same question, and both are deliberate:
/// </para>
/// <list type="table">
/// <item>
///   <term>JSON</term>
///   <description>
///   <c>CommitBare()</c> runs at every value boundary and commits when the structural depth is
///   back at 0. A <em>document-level</em> token — a bare scalar or string written through the
///   writer, the shape hand-written code and a top-level <c>null</c> have — is therefore complete
///   in the buffer the instant the call returns: no hand-off, no second call. (A converter takes a
///   different exit: it writes into <see cref="JsonWriter.Buffer"/> and declares itself with
///   <see cref="JsonWriter.NotifyRawValueWritten"/>.)
///   </description>
/// </item>
/// <item>
///   <term>MessagePack</term>
///   <description>
///   Nothing commits per token. The bytes leave the writer's own span only when the caller says
///   so — reading <see cref="MsgPackWriter.Buffer"/> (the <c>_ = mw.Buffer;</c> generated
///   serializers emit, because they compile into the consumer assembly and can only touch the
///   public surface), or the <em>outermost</em> container closing. A bare value with no
///   enclosing container leaves the tail pending, which is the one rule a custom
///   <see cref="ISerializer{T}"/> author has to know.
///   </description>
/// </item>
/// </list>
/// <para>
/// Below the last value boundary the two agree, and that agreement is the bigger half of the
/// contract: <b>inside</b> a container neither writer commits per token (that is what makes the
/// span cache worth having), closing the outermost container commits the tail in both, reading
/// <see cref="JsonWriter.Buffer"/> / <see cref="MsgPackWriter.Buffer"/> commits whatever is
/// pending in both and is idempotent in both, an empty document commits nothing in both, and
/// both leave nothing pending after the public serializer entry point. The tests below pin the
/// shared invariants once per format and the divergence explicitly — so that a change which
/// quietly makes one writer behave like the other, in <em>either</em> direction, fails here
/// instead of silently changing what a custom serializer sees.
/// </para>
/// <para>
/// Every observation goes through <see cref="CommitSink"/>, which keeps "handed out by
/// <c>GetSpan</c>" apart from "committed by <c>Advance</c>". <c>CommittedLength</c> is exactly
/// what a caller reading the underlying buffer would see, so "the bytes are still staged" is
/// observable without reading <c>Buffer</c> — which would be the very hand-off under test.
/// </para>
/// <para>
/// The writers are <c>ref struct</c>s and cannot be preserved across an <c>await</c>, so each
/// test drives its writer in a synchronous helper that copies the interesting facts out
/// (the <see cref="NestedDocument"/>, <see cref="BufferRead"/> … records below) and asserts
/// afterwards. No assertion is skipped: the helper only reads state, it never asserts.
/// </para>
/// </summary>
public class WriterCommitTimingTests
{
    private const string Json = "json";
    private const string MsgPack = "msgpack";

    // ── The shared invariants: both writers, same answer ───────────────────

    /// <summary>
    /// An empty document writes nothing and commits nothing — in particular it does not hand a
    /// zero-length chunk to the buffer (a writer that flushed unconditionally would).
    /// </summary>
    [Test]
    [Arguments(Json)]
    [Arguments(MsgPack)]
    public async Task EmptyDocument_CommitsNothing(string format)
    {
        var (sink, bytesWritten) = WriteNothing(format);

        await Assert.That(bytesWritten).IsEqualTo(0L);
        await Assert.That(sink.CommittedLength).IsEqualTo(0);
        await Assert
            .That(sink.AdvanceCount)
            .IsEqualTo(0)
            .Because("an empty document must not commit a zero-length chunk");
    }

    private static (CommitSink Sink, long BytesWritten) WriteNothing(string format)
    {
        var sink = new CommitSink();
        if (format == Json)
        {
            var jw = new JsonWriter(sink);
            _ = jw.Buffer;
            return (sink, jw.BytesWritten);
        }
        else
        {
            var mw = new MsgPackWriter(sink);
            _ = mw.Buffer;
            return (sink, mw.BytesWritten);
        }
    }

    /// <summary>
    /// Inside an open container a short token stays in the writer's span — neither writer commits
    /// per token — and closing the outermost container publishes exactly the whole document. This
    /// is the invariant that pays for the span cache, and the one that would break if someone
    /// "simplified" either writer into flushing on every write.
    /// </summary>
    [Test]
    [Arguments(Json)]
    [Arguments(MsgPack)]
    public async Task InnerTokens_StayStaged_UntilTheOutermostContainerCloses(string format)
    {
        var doc = WriteNestedDocument(format);

        await Assert
            .That(doc.Observed.CommittedWhileOpen)
            .IsEqualTo(doc.Observed.CommittedAfterOpen)
            .Because("no token inside an open container may be committed");
        await Assert
            .That(doc.Observed.WrittenWhileOpen)
            .IsGreaterThan((long)doc.Observed.CommittedAfterOpen)
            .Because("the tokens written inside the container are staged, not published");
        await Assert
            .That(doc.Observed.CommittedAfterClose)
            .IsEqualTo((int)doc.Observed.WrittenAfterClose)
            .Because("closing the outermost container commits the tail");
        await Assert.That(doc.Observed.Sink.CommittedBytes).IsEquivalentTo(doc.Expected);
    }

    /// <summary>
    /// A document of one map holding a two-element array — the smallest shape with a value
    /// boundary on both sides of the token loop.
    /// </summary>
    private static (Observation Observed, byte[] Expected) WriteNestedDocument(string format)
    {
        var sink = new CommitSink();
        if (format == Json)
        {
            var jw = new JsonWriter(sink);
            jw.WriteStartObject();
            // Snapshot after the container is open: JSON has already published its `{` (a
            // document-level byte, so CommitBare fired at depth 0), MessagePack has published
            // nothing. That difference belongs to the value boundary, not to the token loop —
            // which is why every comparison below is relative to this snapshot.
            int committedAfterOpen = sink.CommittedLength;

            jw.WritePropertyName("a");
            jw.WriteStartArray();
            jw.WriteNumber(1);
            jw.WriteNumber(2);
            int committedWhileOpen = sink.CommittedLength;
            long writtenWhileOpen = jw.BytesWritten;

            jw.WriteEndArray();
            jw.WriteEndObject();
            var observed = new Observation(
                sink,
                committedAfterOpen,
                committedWhileOpen,
                writtenWhileOpen,
                sink.CommittedLength,
                jw.BytesWritten
            );
            return (observed, Encoding.UTF8.GetBytes("{\"a\":[1,2]}"));
        }
        else
        {
            var mw = new MsgPackWriter(sink);
            mw.WriteStartObject(1);
            int committedAfterOpen = sink.CommittedLength;

            mw.WritePropertyName("a"u8);
            mw.WriteStartArray(2);
            mw.WriteInt32(1);
            mw.WriteInt32(2);
            int committedWhileOpen = sink.CommittedLength;
            long writtenWhileOpen = mw.BytesWritten;

            mw.WriteEndArray();
            mw.WriteEndObject();
            var observed = new Observation(
                sink,
                committedAfterOpen,
                committedWhileOpen,
                writtenWhileOpen,
                sink.CommittedLength,
                mw.BytesWritten
            );
            return (observed, Convert.FromHexString("81A161920102"));
        }
    }

    /// <summary>
    /// Reading <c>Buffer</c> is the hand-off: it publishes the pending tail in both writers, and
    /// reading it twice publishes nothing twice. Duplicated bytes are the failure mode a
    /// forgotten <c>_span = default</c> produces, and no round trip would see it.
    /// </summary>
    [Test]
    [Arguments(Json)]
    [Arguments(MsgPack)]
    public async Task ReadingBuffer_CommitsTheTail_AndIsIdempotent(string format)
    {
        var read = ReadBufferTwice(format);

        await Assert
            .That(read.Before)
            .IsLessThan((int)read.WrittenBefore)
            .Because("something is pending before the hand-off");
        await Assert
            .That(read.First)
            .IsEqualTo((int)read.WrittenBefore)
            .Because("reading Buffer publishes every byte written so far");
        await Assert
            .That(read.AfterSecond)
            .IsEqualTo(read.First)
            .Because("a second read must not publish the same chunk again");
        await Assert
            .That(read.AdvancesAfterSecond)
            .IsEqualTo(read.AdvancesAtFirst)
            .Because("a second read must not commit anything at all");
        await Assert.That(read.CommittedAfterClose).IsEqualTo((int)read.WrittenAfterClose);
        await Assert.That(read.Sink.CommittedBytes).IsEquivalentTo(read.Expected);
    }

    private static BufferRead ReadBufferTwice(string format)
    {
        var sink = new CommitSink();
        if (format == Json)
        {
            var jw = new JsonWriter(sink);
            jw.WriteStartObject();
            jw.WritePropertyName("a");
            jw.WriteNumber(1);

            int before = sink.CommittedLength;
            long writtenBefore = jw.BytesWritten;

            _ = jw.Buffer;
            int first = sink.CommittedLength;
            int advancesAtFirst = sink.AdvanceCount;

            _ = jw.Buffer;
            int afterSecond = sink.CommittedLength;
            int advancesAfterSecond = sink.AdvanceCount;

            jw.WriteEndObject();
            return new BufferRead(
                sink,
                before,
                writtenBefore,
                first,
                advancesAtFirst,
                afterSecond,
                advancesAfterSecond,
                sink.CommittedLength,
                jw.BytesWritten,
                Encoding.UTF8.GetBytes("{\"a\":1}")
            );
        }
        else
        {
            var mw = new MsgPackWriter(sink);
            mw.WriteStartObject(1);
            mw.WritePropertyName("a"u8);
            mw.WriteInt32(1);

            int before = sink.CommittedLength;
            long writtenBefore = mw.BytesWritten;

            _ = mw.Buffer;
            int first = sink.CommittedLength;
            int advancesAtFirst = sink.AdvanceCount;

            _ = mw.Buffer;
            int afterSecond = sink.CommittedLength;
            int advancesAfterSecond = sink.AdvanceCount;

            mw.WriteEndObject();
            return new BufferRead(
                sink,
                before,
                writtenBefore,
                first,
                advancesAtFirst,
                afterSecond,
                advancesAfterSecond,
                sink.CommittedLength,
                mw.BytesWritten,
                Convert.FromHexString("81A16101")
            );
        }
    }

    /// <summary>
    /// A document that crosses several spans still commits completely: the flush that runs when a
    /// span fills up and the flush that runs at the end compose without losing or repeating a
    /// byte. <c>BytesWritten</c> must equal what the buffer actually holds.
    /// </summary>
    [Test]
    [Arguments(Json)]
    [Arguments(MsgPack)]
    public async Task DocumentSpanningSeveralSpans_CommitsEveryByteExactlyOnce(string format)
    {
        var doc = WriteLongDocument(format);

        await Assert.That(doc.Written).IsGreaterThan(512L);
        await Assert
            .That(doc.AdvancedChunks)
            .IsGreaterThan(1)
            .Because("the document must cross more than one span for this test to mean anything");
        await Assert.That(doc.Sink.CommittedLength).IsEqualTo((int)doc.Written);
        await Assert.That(doc.Sink.CommittedBytes[0]).IsEqualTo(doc.First);
        await Assert.That(doc.Sink.CommittedBytes[^doc.FromEndOffset]).IsEqualTo(doc.FromEnd);
    }

    private static LongDocument WriteLongDocument(string format)
    {
        const int Count = 200;
        var sink = new CommitSink();

        if (format == Json)
        {
            var jw = new JsonWriter(sink);
            jw.WriteStartArray();
            for (var i = 0; i < Count; i++)
                jw.WriteNumber(i * 1000);
            jw.WriteEndArray();
            return new LongDocument(
                sink,
                jw.BytesWritten,
                sink.AdvanceCount,
                (byte)'[',
                (byte)']',
                1
            );
        }
        else
        {
            var mw = new MsgPackWriter(sink);
            mw.WriteStartArray(Count);
            for (var i = 0; i < Count; i++)
                mw.WriteInt32(i * 1000);
            mw.WriteEndArray();
            return new LongDocument(
                sink,
                mw.BytesWritten,
                sink.AdvanceCount,
                0xDC, // array 16: 200 elements
                0xD2, // int 32: the last element (199 000) needs five bytes
                5
            );
        }
    }

    /// <summary>
    /// Whatever the two writers do internally, a caller of the public entry point sees the same
    /// thing: after <c>Serialize</c> returns, every byte is in the buffer, and it is exactly the
    /// bytes the <c>byte[]</c> API produces. This is where MessagePack's explicit hand-off
    /// (<c>_ = mw.Buffer;</c>, emitted by the generator) and JSON's depth-0 commit have to meet —
    /// a dropped hand-off in generated code fails here, not in the wild.
    /// </summary>
    [Test]
    [Arguments(Json)]
    [Arguments(MsgPack)]
    public async Task SerializerEntryPoint_LeavesNothingPending(string format)
    {
        var model = AllFormatsModelFactory.Create();
        var sink = new CommitSink();

        byte[] expected;
        if (format == Json)
        {
            JsonSerializer.Serialize(sink, model);
            expected = JsonSerializer.SerializeToUtf8Bytes(model);
        }
        else
        {
            MsgPackSerializer.Serialize(sink, model);
            expected = MsgPackSerializer.SerializeToUtf8Bytes(model);
        }

        await Assert.That(sink.CommittedLength).IsGreaterThan(0);
        await Assert.That(sink.CommittedLength).IsEqualTo(expected.Length);
        await Assert.That(sink.CommittedBytes).IsEquivalentTo(expected);
    }

    // ── The documented divergence: one value, two answers ──────────────────

    /// <summary>
    /// The divergence itself, asserted side by side so it cannot be "fixed" by accident: the same
    /// logical value, written by both writers, is published by JSON the moment the value is
    /// complete and is <em>not</em> published by MessagePack until the caller hands off. Both
    /// halves are intended (see the class comment); a change that makes the two agree turns this
    /// test red on purpose, so that change has to argue its case here first.
    /// </summary>
    [Test]
    public async Task DocumentLevelValue_IsPublishedByJson_AndStagedByMsgPack_OnPurpose()
    {
        var (jsonSink, jsonWritten, msgPackSink, msgPackWritten, msgPackCommittedBefore) =
            WriteOneBareValue();

        await Assert.That(jsonWritten).IsEqualTo(2L);
        await Assert.That(msgPackWritten).IsEqualTo(1L);
        await Assert
            .That(jsonSink.CommittedLength)
            .IsEqualTo(2)
            .Because("JsonWriter.CommitBare() commits a document-level token (depth 0)");
        await Assert
            .That(msgPackCommittedBefore)
            .IsEqualTo(0)
            .Because(
                "MsgPackWriter commits on the Buffer hand-off or when the outermost container closes — never on a token"
            );

        // The hand-off the generated serializers emit; after it the two agree on the bytes being
        // published, they only disagreed on when.
        await Assert.That(msgPackSink.CommittedLength).IsEqualTo(1);
        await Assert.That(msgPackSink.CommittedBytes).IsEquivalentTo(new byte[] { 0x2A });
        await Assert.That(jsonSink.CommittedBytes).IsEquivalentTo(Encoding.UTF8.GetBytes("42"));
    }

    private static (
        CommitSink JsonSink,
        long JsonWritten,
        CommitSink MsgPackSink,
        long MsgPackWritten,
        int MsgPackCommittedBefore
    ) WriteOneBareValue()
    {
        var jsonSink = new CommitSink();
        var jw = new JsonWriter(jsonSink);
        jw.WriteNumber(42);
        long jsonWritten = jw.BytesWritten;

        var msgPackSink = new CommitSink();
        var mw = new MsgPackWriter(msgPackSink);
        mw.WriteInt32(42);
        long msgPackWritten = mw.BytesWritten;
        int msgPackCommittedBefore = msgPackSink.CommittedLength;

        _ = mw.Buffer;
        return (jsonSink, jsonWritten, msgPackSink, msgPackWritten, msgPackCommittedBefore);
    }

    /// <summary>
    /// JSON's own answer, one case per token kind: every document-level value is complete in the
    /// buffer when the call returns, with nothing extra asked of the caller. A document never
    /// opens a container, so a hand-off would never come — this is the half a converter and
    /// hand-written code rely on.
    /// </summary>
    [Test]
    [Arguments("null", "null")]
    [Arguments("true", "true")]
    [Arguments("number", "42")]
    [Arguments("string", "\"hi\"")]
    public async Task JsonDocumentLevelToken_IsCommittedWithoutAnyExtraCall(
        string kind,
        string expectedText
    )
    {
        var (sink, written) = WriteJsonBareToken(kind);

        await Assert.That(written).IsGreaterThan(0L);
        await Assert
            .That(sink.CommittedLength)
            .IsEqualTo((int)written)
            .Because($"a document-level {kind} token commits at depth 0");
        await Assert.That(sink.CommittedBytes).IsEquivalentTo(Encoding.UTF8.GetBytes(expectedText));
    }

    private static (CommitSink Sink, long BytesWritten) WriteJsonBareToken(string kind)
    {
        var sink = new CommitSink();
        var jw = new JsonWriter(sink);
        switch (kind)
        {
            case "null":
                jw.WriteNull();
                break;
            case "true":
                jw.WriteBoolean(true);
                break;
            case "number":
                jw.WriteNumber(42);
                break;
            default:
                jw.WriteString("hi");
                break;
        }
        return (sink, jw.BytesWritten);
    }

    /// <summary>
    /// MessagePack's own answer, one case per token kind: a bare value stays staged, and the
    /// <c>Buffer</c> hand-off — the only public flush exit — publishes exactly the staged bytes.
    /// </summary>
    [Test]
    [Arguments("null", "C0")]
    [Arguments("true", "C3")]
    [Arguments("int", "2A")]
    [Arguments("string", "A26869")]
    public async Task MsgPackBareValue_StaysStagedUntilTheHandoff(string kind, string expectedHex)
    {
        var (sink, written, committedBefore) = WriteMsgPackBareValue(kind);

        await Assert.That(written).IsGreaterThan(0L);
        await Assert
            .That(committedBefore)
            .IsEqualTo(0)
            .Because(
                $"a bare {kind} value has no enclosing container to close, so nothing commits on its own"
            );
        await Assert.That(sink.CommittedLength).IsEqualTo((int)written);
        await Assert.That(sink.CommittedBytes).IsEquivalentTo(Convert.FromHexString(expectedHex));
    }

    private static (CommitSink Sink, long BytesWritten, int CommittedBefore) WriteMsgPackBareValue(
        string kind
    )
    {
        var sink = new CommitSink();
        var mw = new MsgPackWriter(sink);
        switch (kind)
        {
            case "null":
                mw.WriteNull();
                break;
            case "true":
                mw.WriteBoolean(true);
                break;
            case "int":
                mw.WriteInt32(42);
                break;
            default:
                mw.WriteString("hi"u8);
                break;
        }
        long written = mw.BytesWritten;
        int committedBefore = sink.CommittedLength;

        _ = mw.Buffer;
        return (sink, written, committedBefore);
    }

    // ── Plain snapshots (a ref struct cannot cross an await) ───────────────

    /// <summary>Depth taken while the outer container is open, and after it closes.</summary>
    private sealed record Observation(
        CommitSink Sink,
        int CommittedAfterOpen,
        int CommittedWhileOpen,
        long WrittenWhileOpen,
        int CommittedAfterClose,
        long WrittenAfterClose
    );

    /// <summary>The two <c>Buffer</c> reads, and the document after it closes.</summary>
    private sealed record BufferRead(
        CommitSink Sink,
        int Before,
        long WrittenBefore,
        int First,
        int AdvancesAtFirst,
        int AfterSecond,
        int AdvancesAfterSecond,
        int CommittedAfterClose,
        long WrittenAfterClose,
        byte[] Expected
    );

    /// <summary>A document long enough to cross several spans, plus its first and last markers.</summary>
    private sealed record LongDocument(
        CommitSink Sink,
        long Written,
        int AdvancedChunks,
        byte First,
        byte FromEnd,
        int FromEndOffset
    );

    /// <summary>
    /// <see cref="IBufferWriter{T}"/> that keeps two facts apart: how many bytes it has
    /// <em>handed out</em> (<c>GetSpan</c>) and how many the writer has <em>committed</em>
    /// (<c>Advance</c>). <c>CommittedLength</c> is what a caller reading the buffer would see, so
    /// "still staged" is observable without reading <c>Buffer</c>.
    /// </summary>
    /// <remarks>
    /// <c>GetSpan</c> hands out <em>exactly</em> the requested size (the production writers ask
    /// for at least their own <c>MinSpanSize</c>), so a document that is longer than one span is
    /// forced across several spans whatever that constant happens to be — the multi-span test does
    /// not rest on an implementation detail. <c>Advance</c> rejects a count larger than the bytes
    /// handed out, which turns a cursor bug into a failure instead of silent corruption;
    /// <c>AdvanceCount</c> makes a duplicated commit visible even when the duplicated chunk would
    /// be empty.
    /// </remarks>
    private sealed class CommitSink : IBufferWriter<byte>
    {
        private byte[] _buffer;
        private int _committed;
        private int _handedOut;

        internal CommitSink(int capacity = 512) => _buffer = new byte[capacity];

        /// <summary>Bytes the writer has committed — i.e. what the buffer now owns.</summary>
        internal int CommittedLength => _committed;

        /// <summary>How many chunks were committed so far.</summary>
        internal int AdvanceCount { get; private set; }

        /// <summary>A copy of everything committed, in write order.</summary>
        internal byte[] CommittedBytes => _buffer.AsSpan(0, _committed).ToArray();

        public void Advance(int count)
        {
            if (count < 0 || count > _handedOut)
                throw new InvalidOperationException(
                    $"Advance({count}) with only {_handedOut} byte(s) handed out"
                );
            _committed += count;
            _handedOut = 0;
            AdvanceCount++;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            _ = GetSpan(sizeHint);
            return _buffer.AsMemory(_committed, _handedOut);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            if (sizeHint < 1)
                sizeHint = 1;
            if (_committed + sizeHint > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_committed + sizeHint, _buffer.Length * 2));
            // Hand out exactly what was asked for, never the whole tail: a span the writer
            // cannot see the end of would hide how many flushes a document really costs, and
            // would make "crosses several spans" depend on the writer's MinSpanSize.
            _handedOut = sizeHint;
            return _buffer.AsSpan(_committed, _handedOut);
        }
    }
}
