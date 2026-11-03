namespace PicoMsgPack.Tests;

/// <summary>
/// "I am done" must be sayable. <see cref="MsgPackWriter"/> is a <c>ref struct</c> that writes
/// into a span of its own, and until this file's subject existed its only commit exits were
/// <see cref="MsgPackWriter.Buffer"/> (a read, used as a hand-off), the outermost container
/// closing — and an <c>internal</c> alias the consumer assembly cannot even see. So the two
/// shapes that strand bytes were both unreachable from a caller:
/// <list type="bullet">
///   <item>a <b>bare value</b> (no enclosing container): nothing will ever close it, so a custom
///   <see cref="ISerializer{T}"/> that forgets <c>_ = mw.Buffer;</c> hands back an empty
///   buffer;</item>
///   <item>an <b>early exit</b> from inside a container: the tail stays in the span cache and
///   nothing throws — the same hole <c>JsonWriter.Dispose</c> closed in phase 1
///   (<see cref="PicoJetson.JsonWriter.Flush"/>).</item>
/// </list>
/// <para>
/// Two members close it, with the reach JSON's pair has, because the two writers are one
/// family: <see cref="MsgPackWriter.Flush"/> — "commit what I have written so far", no close, so
/// the bytes stay a legal prefix the caller may keep writing into; and
/// <see cref="MsgPackWriter.Dispose"/> — the same commit behind a scope hook, so
/// <c>using var mw = new MsgPackWriter(buf);</c> hands the tail over on the happy path, on an
/// early <c>return</c> and on an exception alike. Neither ever throws and neither invents a
/// byte, which is why <c>Dispose</c> can sit next to code that failed.
/// </para>
/// <para>
/// Bytes are asserted, not intent: every test reads <c>buf.WrittenSpan</c>, because "the writer
/// thinks it committed" is exactly the assumption that was wrong before. No test keeps a
/// <c>ref struct</c> across an <c>await</c> — that is illegal — so each one drives its writer in
/// a synchronous helper and asserts afterwards.
/// </para>
/// </remarks>
public class MsgPackWriterCommitTests
{
    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    // ── Flush: commit the tail, keep the container open ─────────────────────

    /// <summary>
    /// The bare-value hole, from the caller's side: a string written with no container around it
    /// leaves nothing in the buffer at all, and <see cref="MsgPackWriter.Flush"/> is what the
    /// caller says instead of reading <see cref="MsgPackWriter.Buffer"/> for its side effect.
    /// </summary>
    [Test]
    public async Task BareValue_StaysPending_UntilTheCallerCommits()
    {
        var (before, after, pending, committed) = WriteBareStringThenFlush();

        await Assert.That(before).IsEqualTo(string.Empty).Because("nothing closes a bare value");
        await Assert.That(after).IsEqualTo("A3616263").Because("fixstr 'abc'");
        await Assert
            .That(pending)
            .IsEqualTo((long)committed)
            .Because("a commit leaves nothing pending");
    }

    private static (
        string Before,
        string After,
        long Pending,
        int Committed
    ) WriteBareStringThenFlush()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteString("abc"u8);
        var before = Hex(buf.WrittenSpan);
        var pending = mw.BytesWritten;
        mw.Flush();
        return (before, Hex(buf.WrittenSpan), pending, buf.WrittenCount);
    }

    /// <summary>
    /// A commit is not a close: the map is still open afterwards, so the caller keeps writing and
    /// closes it itself, and the concatenation is the document it would have written without the
    /// flush. <c>82 00 2A</c> is <c>{0: 42}</c> of the promised two entries.
    /// </summary>
    [Test]
    public async Task Flush_hands_the_pending_bytes_over_without_closing_the_map()
    {
        var (flushed, closed) = FlushHalfAMap();

        await Assert.That(flushed).IsEqualTo("82002A").Because("the half-written map, committed");
        await Assert.That(closed).IsEqualTo("82002A01A26869").Because("the same map, finished");
    }

    private static (string Flushed, string Closed) FlushHalfAMap()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.Flush();
        var flushed = Hex(buf.WrittenSpan);
        mw.WriteInt32(1);
        mw.WriteString("hi"u8);
        mw.WriteEndObject();
        return (flushed, Hex(buf.WrittenSpan));
    }

    /// <summary>
    /// A second flush with nothing pending must not hand the same chunk over twice (the failure
    /// mode the span cache makes possible), and a flush must agree with the hand-off it replaces.
    /// </summary>
    [Test]
    public async Task Flush_is_idempotent_and_agrees_with_the_buffer_hand_off()
    {
        var viaFlush = HalfAMapThenFlushAlreadyFlushed();
        var viaBuffer = HalfAMapThroughBufferHandOff();

        await Assert.That(viaFlush).IsEqualTo("82002A").Because("idempotent flush");
        await Assert
            .That(viaFlush)
            .IsEqualTo(viaBuffer)
            .Because("_ = mw.Buffer is the same commit");
    }

    private static string HalfAMapThenFlushAlreadyFlushed()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.Flush();
        mw.Flush();
        return Hex(buf.WrittenSpan);
    }

    private static string HalfAMapThroughBufferHandOff()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        _ = mw.Buffer;
        return Hex(buf.WrittenSpan);
    }

    /// <summary>
    /// Span-cache sized payload: forty members cross several span refills, so a chunk that is
    /// dropped by a flush that only knows about the last one is visible as a byte count, not as a
    /// plausible shorter document.
    /// </summary>
    [Test]
    public async Task Flush_commits_every_chunk_of_a_payload_wider_than_one_span()
    {
        var (written, committed) = WriteFortyMembersAndFlush();

        await Assert
            .That(committed)
            .IsEqualTo(written)
            .Because("BytesWritten is what the buffer holds");
        await Assert.That(written).IsGreaterThan(256L).Because("several spans' worth of bytes");
    }

    private static (long Written, int Committed) WriteFortyMembersAndFlush()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(40);
        for (var i = 0; i < 40; i++)
        {
            mw.WriteInt32(i);
            mw.WriteString("abcdefghijklmnopqrstuvwxyz0123456789"u8);
        }
        var written = mw.BytesWritten;
        mw.Flush();
        return (written, buf.WrittenCount);
    }

    // ── Dispose: the scope hook ────────────────────────────────────────────

    /// <summary>
    /// The exit that used to lose the tail: <c>return</c> from inside the <c>using</c> block. The
    /// helper returns the buffer length it saw <em>at</em> the early return (zero), so the
    /// assertion after it is a statement about <c>Dispose</c> and not about the writer having
    /// been tidy on its own.
    /// </summary>
    [Test]
    public async Task Using_commits_the_tail_when_the_scope_exits_with_an_early_return()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var lengthAtTheEarlyReturn = WriteHalfAMapAndReturn(buf);

        await Assert
            .That(lengthAtTheEarlyReturn)
            .IsEqualTo(0)
            .Because("at the early return the bytes were still in the span cache");
        await Assert
            .That(Hex(buf.WrittenSpan))
            .IsEqualTo("82002A")
            .Because("Dispose published them");
    }

    private static int WriteHalfAMapAndReturn(ArrayBufferWriter<byte> buf)
    {
        using var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        return buf.WrittenCount; // early exit: Dispose runs after this value is computed
    }

    /// <summary>
    /// The same hole on the exception path. <c>Dispose</c> must never throw over a document that
    /// is still open — otherwise it would replace the caller's exception with its own, and the
    /// bytes it exists to save would be lost anyway.
    /// </summary>
    [Test]
    public async Task Using_commits_the_tail_when_the_scope_exits_by_exception()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var rethrown = WriteHalfAMapAndThrow(buf);

        await Assert.That(rethrown).IsTrue();
        await Assert.That(Hex(buf.WrittenSpan)).IsEqualTo("82002A");
    }

    private static bool WriteHalfAMapAndThrow(ArrayBufferWriter<byte> buf)
    {
        try
        {
            using var mw = new MsgPackWriter(buf);
            mw.WriteStartObject(2);
            mw.WriteInt32(0);
            mw.WriteInt32(42);
            throw new InvalidOperationException("the caller's problem, not the writer's");
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// <c>Dispose</c> commits and says nothing else: no byte is invented, no depth is popped, so
    /// the writer can still be closed properly afterwards. This is the difference between it and
    /// the outermost <see cref="MsgPackWriter.WriteEndObject"/>, which also closes.
    /// </summary>
    [Test]
    public async Task Dispose_commits_without_closing_the_container()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(2);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.Dispose();
        var disposed = Hex(buf.WrittenSpan);
        mw.WriteInt32(1);
        mw.WriteString("hi"u8);
        mw.WriteEndObject();

        await Assert.That(disposed).IsEqualTo("82002A").Because("no end marker was invented");
        await Assert
            .That(Hex(buf.WrittenSpan))
            .IsEqualTo("82002A01A26869")
            .Because("still writable");
    }

    /// <summary>
    /// A complete document is not changed by <c>Dispose</c>: the outermost close already
    /// committed, so a second commit has nothing to hand over — the duplicate-bytes failure mode
    /// of a scope hook that re-advances.
    /// </summary>
    [Test]
    public async Task Dispose_over_a_closed_document_adds_no_byte()
    {
        var (closed, afterDispose) = CloseAMapThenDisposeTwice();

        await Assert.That(afterDispose).IsEqualTo(closed);
        await Assert.That(afterDispose).IsEqualTo("81002A");
    }

    private static (string Closed, string AfterDispose) CloseAMapThenDisposeTwice()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(1);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.WriteEndObject();
        var closed = Hex(buf.WrittenSpan);
        mw.Dispose();
        mw.Dispose();
        return (closed, Hex(buf.WrittenSpan));
    }

    /// <summary>
    /// The writer's life does not end at <c>Dispose</c>: being idempotent and non-closing also
    /// means it stays usable, which is what lets a caller scope one writer per document inside a
    /// loop without a second constructor.
    /// </summary>
    [Test]
    public async Task Dispose_is_not_a_tombstone_the_writer_carries_on()
    {
        await Assert.That(TwoDocumentsInOneScope()).IsEqualTo("81002A8101A161");
    }

    private static string TwoDocumentsInOneScope()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.WriteStartObject(1);
        mw.WriteInt32(0);
        mw.WriteInt32(42);
        mw.Dispose();
        mw.WriteStartObject(1);
        mw.WriteInt32(1);
        mw.WriteString("a"u8);
        mw.Dispose();
        return Hex(buf.WrittenSpan);
    }

    /// <summary>An untouched writer disposes into nothing — no throw, no zero-length chunk.</summary>
    [Test]
    public async Task Dispose_on_an_untouched_writer_writes_nothing()
    {
        var (written, count) = DisposeAnUntouchedWriter();

        await Assert.That(count).IsEqualTo(0);
        await Assert.That(written).IsEqualTo(0L);
    }

    private static (long Written, int Count) DisposeAnUntouchedWriter()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var mw = new MsgPackWriter(buf);
        mw.Dispose();
        mw.Flush();
        return (mw.BytesWritten, buf.WrittenCount);
    }
}
