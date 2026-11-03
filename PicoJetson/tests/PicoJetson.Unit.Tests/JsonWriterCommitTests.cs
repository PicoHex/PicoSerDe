namespace PicoJetson.Tests;

/// <summary>
/// "I am done" must be sayable. The writer's only commit point used to be the outermost
/// <see cref="JsonWriter.WriteEndObject"/> / <see cref="JsonWriter.WriteEndArray"/> — a
/// <c>ref struct</c> with no <c>Dispose</c> and no public <c>Flush</c> — so every early exit
/// from inside a container stranded the tail in the private span cache: a small document kept
/// just <c>{</c>, a large one stopped at the last 256-B refill, and nothing threw
/// (<c>PicoJetson/README.md</c> → "Closing a container is the commit point").
/// </summary>
/// <remarks>
/// <para>
/// Two members close that hole, and their reach differs on purpose:
/// </para>
/// <list type="bullet">
///   <item><see cref="JsonWriter.Flush"/> — "commit what I have written so far". It does
///   <em>not</em> close the container, so the bytes stay a legal prefix of the document and the
///   caller may keep writing afterwards.</item>
///   <item><see cref="JsonWriter.Dispose"/> — "I am done, whatever happened". The same commit
///   behind a scope hook, so <c>using var w = new JsonWriter(buf);</c> hands the tail over on the
///   happy path, on an early <c>return</c> and on an exception alike. Neither of them ever
///   throws, and neither invents a closing token.</item>
/// </list>
/// <para>
/// The bytes are asserted, not the intent: every test reads <c>buf.WrittenSpan</c>, because "the
/// writer thinks it committed" is exactly the assumption that failed before. No test keeps a
/// writer across an <c>await</c> — a <c>ref struct</c> forbids it — so the writing lives in a
/// small synchronous helper and the assertions run after it.
/// </para>
/// </remarks>
public class JsonWriterCommitTests
{
    private static string Str(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    // ── Flush: commit the tail, keep the container open ─────────────────────

    [Test]
    public async Task Flush_hands_the_pending_bytes_to_the_buffer_without_closing_the_container()
    {
        // Before: the object's two members sat in the span cache and the buffer held `{` —
        // BytesWritten said 10, the caller's buffer said 1, and nobody was told.
        var (flushed, closed) = FlushHalfAnObject();

        await Assert.That(flushed).IsEqualTo("{\"a\":1,\"b\":\"x\"");

        // Not a close: the document is a legal *prefix* and the writer still knows the object
        // is open, so the caller keeps writing and closes it itself.
        await Assert.That(closed).IsEqualTo("""{"a":1,"b":"x","c":true}""");
    }

    private static (string Flushed, string Closed) FlushHalfAnObject()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WritePropertyName("b"u8);
        w.WriteString("x");
        w.Flush();
        var flushed = Str(buf.WrittenSpan);
        // The claim in the remark above, from the other side: a commit leaves nothing pending.
        var pending = w.BytesWritten != buf.WrittenCount;
        w.WritePropertyName("c"u8);
        w.WriteBoolean(true);
        w.WriteEndObject();
        return (pending ? $"<{pending} bytes pending>" : flushed, Str(buf.WrittenSpan));
    }

    [Test]
    public async Task Flush_after_an_early_exit_keeps_the_tail_the_old_contract_lost()
    {
        // The failure the README documents with numbers: 40 members, a span refill, then a
        // `return` from inside the object. The buffer used to stop at the last committed span;
        // now the caller says "commit" and gets everything it wrote.
        var buf = new ArrayBufferWriter<byte>(8);
        WriteFortyMembersAndFlush(buf);
        var json = Str(buf.WrittenSpan);

        await Assert.That(json.StartsWith("{\"p0\":0,", StringComparison.Ordinal)).IsTrue();
        await Assert.That(json.EndsWith(",\"p39\":39", StringComparison.Ordinal)).IsTrue();
        // A fragment, honestly: the container was never closed, and Flush does not invent one.
        await Assert.That(PicoDocument.IsValid(buf.WrittenSpan)).IsFalse();
    }

    /// <summary>Starts an object, writes 40 members, flushes, returns — no close.</summary>
    private static void WriteFortyMembersAndFlush(IBufferWriter<byte> buffer)
    {
        var w = new JsonWriter(buffer);
        w.WriteStartObject();
        for (var i = 0; i < 40; i++)
        {
            w.WritePropertyName(Encoding.UTF8.GetBytes($"p{i}"));
            w.WriteNumber(i);
        }
        w.Flush();
    }

    [Test]
    public async Task Flush_never_throws_and_commits_nothing_when_nothing_is_pending()
    {
        // "I have written nothing" and "I already flushed" are both legal: a Flush that threw
        // would put a call in the way of the very `finally` this member exists for.
        var (empty, closed) = FlushTwiceThenWriteAnEmptyObject();
        await Assert.That(empty).IsEqualTo("");
        await Assert.That(closed).IsEqualTo("{}");
    }

    private static (string Empty, string Closed) FlushTwiceThenWriteAnEmptyObject()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.Flush();
        w.Flush();
        var empty = Str(buf.WrittenSpan);
        w.WriteStartObject();
        w.WriteEndObject();
        w.Flush();
        return (empty, Str(buf.WrittenSpan));
    }

    [Test]
    public async Task Flush_then_writing_more_appends_instead_of_overwriting()
    {
        // The span cache must be dropped by the commit, or the next write lands in memory the
        // buffer already owns (the duplicated-bytes bug this cache has had before).
        var (mid, final) = FlushBetweenTwoHalves();
        await Assert.That(mid).IsEqualTo("""{"a":""");
        await Assert.That(final).IsEqualTo("""{"a":1}""");
    }

    private static (string Mid, string Final) FlushBetweenTwoHalves()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.Flush();
        var mid = Str(buf.WrittenSpan);
        w.WriteNumber(1);
        w.WriteEndObject();
        return (mid, Str(buf.WrittenSpan));
    }

    [Test]
    public async Task Flush_is_available_at_any_depth_and_does_not_disturb_the_separators()
    {
        // Flush inside a nested array inside an object: the comma state lives in the writer,
        // not in the buffer, so a commit in the middle must not add or lose a separator.
        var json = FlushInsideANestedArray();
        await Assert.That(json).IsEqualTo("""{"list":[1,2],"after":3}""");
        await Assert.That(PicoDocument.IsValid(Encoding.UTF8.GetBytes(json))).IsTrue();
    }

    private static string FlushInsideANestedArray()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("list"u8);
        w.WriteStartArray();
        w.WriteNumber(1);
        w.Flush();
        w.WriteNumber(2);
        w.Flush();
        w.WriteEndArray();
        w.WritePropertyName("after"u8);
        w.WriteNumber(3);
        w.WriteEndObject();
        return Str(buf.WrittenSpan);
    }

    // ── Dispose: commit the tail, never throw, never close ──────────────────

    [Test]
    public async Task Using_commits_the_tail_when_the_scope_ends_with_a_container_still_open()
    {
        // The shape every helper shows: a container opened, no `using` to write, an early exit.
        var buf = new ArrayBufferWriter<byte>(8);
        WriteAndNeverCloseTheObject(buf);
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("""{"a":1""");
    }

    private static void WriteAndNeverCloseTheObject(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
    }

    [Test]
    public async Task Using_commits_the_tail_when_the_scope_exits_with_an_early_return()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        await Assert.That(WriteUserAndReturnEarly(buf)).IsEqualTo("early");
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("""{"name":"ada","id":7""");
    }

    private static string WriteUserAndReturnEarly(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("name"u8);
        w.WriteString("ada");
        w.WritePropertyName("id"u8);
        w.WriteNumber(7);
        return "early"; // the `WriteEndObject` the happy path would have written
    }

    [Test]
    public async Task Using_commits_the_tail_when_the_scope_exits_by_exception()
    {
        // The reason Dispose must not throw: it runs on the way out of an exception that is
        // already unwinding, and it must still hand over what was written.
        var buf = new ArrayBufferWriter<byte>(8);
        var threw = false;
        try
        {
            WriteThenThrow(buf);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("{\"a\":\"boom\"");
    }

    private static void WriteThenThrow(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteString("boom");
        throw new InvalidOperationException("downstream failure");
    }

    [Test]
    public async Task Using_does_not_throw_over_a_pending_property_name()
    {
        // A `}` invented here would turn `{"a":` into `{"a":}` — illegal JSON *and* a lie about
        // what the caller wrote. Dispose commits and says nothing.
        var buf = new ArrayBufferWriter<byte>(8);
        WriteANameWithNoValue(buf);
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("""{"a":""");
    }

    private static void WriteANameWithNoValue(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
    }

    [Test]
    public async Task Using_a_complete_document_writes_the_same_bytes_and_no_more()
    {
        // Dispose must be a commit, not a second commit: the closing brace's own flush already
        // ran at depth 0, so nothing is pending by then.
        var buf = new ArrayBufferWriter<byte>(8);
        WriteAWholeDocument(buf);
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("""{"a":1,"b":[2,3]}""");
    }

    private static void WriteAWholeDocument(IBufferWriter<byte> buffer)
    {
        using var w = new JsonWriter(buffer);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WritePropertyName("b"u8);
        w.WriteStartArray();
        w.WriteNumber(2);
        w.WriteNumber(3);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    [Test]
    public async Task Dispose_is_idempotent_safe_on_an_untouched_writer_and_the_writer_stays_usable()
    {
        // Three properties in one place, because they are one design decision: Dispose is a
        // commit, so calling it twice must not duplicate bytes, calling it on a writer that
        // wrote nothing must not throw, and (unlike a `ref struct` scope hook's usual meaning)
        // it does not end the writer's life — the state it clears is the span cache only.
        var (untouched, afterDispose, pending) = DisposeTwiceThenWrite();
        await Assert.That(untouched).IsEqualTo("");
        await Assert.That(afterDispose).IsEqualTo("{\"a\":null");
        await Assert.That(pending).IsEqualTo("");
    }

    private static (string Untouched, string AfterDispose, string Pending) DisposeTwiceThenWrite()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.Dispose();
        w.Dispose();
        var untouched = Str(buf.WrittenSpan);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNull();
        w.Dispose();
        w.Dispose();
        var afterDispose = Str(buf.WrittenSpan);
        var pending = w.BytesWritten != buf.WrittenCount ? "bytes still pending" : "";
        return (untouched, afterDispose, pending);
    }
}
