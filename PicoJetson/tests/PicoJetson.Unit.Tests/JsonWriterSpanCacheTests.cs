namespace PicoJetson.Tests;

/// <summary>
/// A buffer writer that hands out tiny, fixed-size spans.
/// </summary>
/// <remarks>
/// The point is to force the writer's cached span to run out in the middle of tokens: with
/// <c>chunk = 1</c> every single byte crosses a span boundary, and every chunk size from 1
/// to <c>n</c> covers a different "the next token is 1 / 2 / 3 bytes too long for what is
/// left" remainder. Any off-by-one in <c>Ensure</c> / <c>Flush</c> shows up as a byte
/// difference against the same document written through an <see cref="ArrayBufferWriter{T}"/>.
/// <para>
/// <see cref="GetSpan"/> never returns less than the requested size hint (the
/// <see cref="IBufferWriter{T}"/> contract) but otherwise caps the span at <c>chunk</c>.
/// </para>
/// </remarks>
public sealed class TinyChunkBufferWriter : IBufferWriter<byte>
{
    private readonly int _chunk;
    private byte[] _buffer;
    private int _index;

    public TinyChunkBufferWriter(int chunk)
    {
        _chunk = Math.Max(chunk, 1);
        _buffer = new byte[_chunk];
    }

    /// <summary>How many times the writer asked for a span (i.e. how often Flush + refetch ran).</summary>
    public int GetSpanCalls { get; private set; }

    /// <summary>How many times the writer committed bytes.</summary>
    public int AdvanceCalls { get; private set; }

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _index);

    public void Advance(int count)
    {
        AdvanceCalls++;
        if (count < 0 || _index + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(
                nameof(count),
                "Advance past the end of the buffer."
            );
        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        GetSpanCalls++;
        var need = Math.Max(sizeHint, 1);
        if (_index + need > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_index + need, _buffer.Length * 2));
        var available = Math.Min(Math.Max(_chunk, need), _buffer.Length - _index);
        return _buffer.AsSpan(_index, available);
    }
}

/// <summary>
/// The span cache in <see cref="JsonWriter"/>: the writer takes a chunk of the buffer once,
/// writes tokens straight into it, and commits when the chunk is full or when something
/// outside the writer needs to see the bytes. These tests pin the two ways that can go wrong
/// — bytes left uncommitted, and bytes committed twice — plus the copy semantics a
/// <c>ref struct</c> with a cursor inside imposes on every caller.
/// </summary>
public class JsonWriterSpanCacheTests
{
    /// <summary>Writes a whole document; <c>ref</c> because a copy would carry its own cursor.</summary>
    private delegate void WriterAction(ref JsonWriter w);

    private static byte[] WriteThrough(
        IBufferWriter<byte> buffer,
        WriterAction write,
        bool indented = false
    )
    {
        var w = new JsonWriter(buffer, indented: indented);
        write(ref w);
        return buffer switch
        {
            ArrayBufferWriter<byte> ab => ab.WrittenSpan.ToArray(),
            TinyChunkBufferWriter tiny => tiny.WrittenSpan.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(buffer)),
        };
    }

    private static string Str(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);

    private static async Task AssertBytes(byte[] expected, byte[] actual, string context)
    {
        if (actual.AsSpan().SequenceEqual(expected))
            return;
        await Assert
            .That($"'{Str(actual)}'")
            .IsEqualTo($"'{Str(expected)}'")
            .Because($"{context} must be byte-identical");
    }

    /// <summary>
    /// The corpus: every write path of the writer, including the "payload too long for a
    /// cache slot" branch and the escaping ones.
    /// </summary>
    private static IEnumerable<(string Name, WriterAction Write, bool Indented)> Documents()
    {
        yield return ("empty", (WriterAction)((ref JsonWriter w) => { }), false);
        yield return ("null", (WriterAction)((ref JsonWriter w) => w.WriteNull()), false);
        yield return ("true", (WriterAction)((ref JsonWriter w) => w.WriteBoolean(true)), false);
        yield return (
            "empty object",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WriteEndObject();
                }
            ),
            false
        );
        yield return (
            "empty array",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartArray();
                    w.WriteEndArray();
                }
            ),
            false
        );
        yield return (
            "numbers",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartArray();
                    w.WriteNumber(int.MinValue);
                    w.WriteNumber(int.MaxValue);
                    w.WriteNumber(long.MinValue);
                    w.WriteNumber(long.MaxValue);
                    w.WriteNumber(ulong.MaxValue);
                    w.WriteNumber((short)-1);
                    w.WriteNumber((ushort)1);
                    w.WriteNumber((sbyte)-2);
                    w.WriteNumber((byte)2);
                    w.WriteNumber(0u);
                    w.WriteNumber(0.0);
                    w.WriteNumber(-0.0);
                    w.WriteNumber(double.Epsilon);
                    w.WriteNumber(double.MaxValue);
                    w.WriteNumber(double.MinValue);
                    w.WriteNumber(float.MinValue);
                    w.WriteNumber(0.1f);
                    w.WriteNumber(79228162514264337593543950335m);
                    w.WriteNumber(-79228162514264337593543950335m);
                    w.WriteEndArray();
                }
            ),
            false
        );
        yield return (
            "escapes",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartArray();
                    w.WriteString("\"quote\""u8);
                    w.WriteString("back\\slash"u8);
                    w.WriteString("tab\there"u8);
                    w.WriteString("u+0001:\u0001"u8);
                    w.WriteString("非 ASCII 漢字"u8);
                    w.WriteString("emoji 😀🎉"u8);
                    w.WriteString("surrogate 𐒠𝄞"u8);
                    w.WriteString("mixed \"quoted\" \\ and\t tab"u8);
                    w.WriteString("chars \"quoted\" \\ and\t tab");
                    w.WriteString("chars 非 ASCII 😀");
                    w.WriteEndArray();
                }
            ),
            false
        );
        yield return (
            "prop names escaped",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("plain"u8);
                    w.WriteNumber(1);
                    w.WritePropertyName("quo\"te"u8);
                    w.WriteNumber(2);
                    w.WritePropertyName("tab\there"u8);
                    w.WriteNumber(3);
                    w.WritePropertyName("漢字"u8);
                    w.WriteNumber(4);
                    w.WriteEndObject();
                }
            ),
            false
        );
        yield return (
            "indented",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("a"u8);
                    w.WriteStartArray();
                    w.WriteNumber(1);
                    w.WriteStartObject();
                    w.WritePropertyName("b"u8);
                    w.WriteString("c"u8);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WritePropertyName("d"u8);
                    w.WriteNull();
                    w.WriteEndObject();
                }
            ),
            true
        );
        yield return (
            "bare int",
            (WriterAction)((ref JsonWriter w) => w.WriteNumber(123456789)),
            false
        );
        yield return (
            "bare string",
            (WriterAction)(
                (ref JsonWriter w) => w.WriteString(Encoding.UTF8.GetBytes("bare \"value\""))
            ),
            false
        );
        yield return (
            "raw value",
            (WriterAction)(
                (ref JsonWriter w) =>
                {
                    w.WriteStartObject();
                    w.WritePropertyName("raw"u8);
                    w.WriteRawValue("{\"x\":1}"u8);
                    w.WriteRawValue(ReadOnlySpan<char>.Empty);
                    w.WriteEndObject();
                }
            ),
            false
        );
    }

    /// <summary>
    /// A document long enough to need many chunks, with every token kind in it. Also
    /// exercises the <c>payload &gt;= MinSpanSize</c> shortcut in <c>WriteRaw</c>.
    /// </summary>
    private static void WriteLargeDocument(ref JsonWriter w)
    {
        w.WriteStartObject();
        for (var i = 0; i < 200; i++)
        {
            w.WritePropertyName(Encoding.UTF8.GetBytes("field_" + i));
            w.WriteNumber(i);
        }
        w.WritePropertyName("strings"u8);
        w.WriteStartArray();
        for (var i = 0; i < 8; i++)
            w.WriteString(Encoding.UTF8.GetBytes(new string((char)('a' + i), 260)));
        w.WriteEndArray();
        w.WritePropertyName("nested"u8);
        WriteDeep(ref w, 20);
        w.WriteEndObject();
    }

    private static void WriteDeep(ref JsonWriter w, int levels)
    {
        if (levels == 0)
        {
            w.WriteNumber(1729);
            return;
        }
        w.WriteStartArray();
        WriteDeep(ref w, levels - 1);
        w.WriteEndArray();
    }

    // ── cross-buffer-boundary ────────────────────────────────────────────────

    [Test]
    public async Task EveryChunkSizeReproducesTheSameBytes()
    {
        foreach (var (name, write, indented) in Documents())
        {
            var expected = WriteThrough(new ArrayBufferWriter<byte>(8), write, indented);
            for (var chunk = 1; chunk <= 41; chunk++)
            {
                var tiny = new TinyChunkBufferWriter(chunk);
                var actual = WriteThrough(tiny, write, indented);
                await AssertBytes(expected, actual, $"'{name}' at chunk size {chunk}");
                if (expected.Length > 0)
                    await Assert.That(tiny.GetSpanCalls).IsGreaterThan(0);
            }
        }
    }

    [Test]
    public async Task ChunkEndingOneTwoThreeBytesShortOfTheNextTokenIsHarmless()
    {
        // Shift the chunk end across the whole token: the writer must refetch rather than
        // spill into bytes that are no longer its own.
        for (var pad = 0; pad < 8; pad++)
        {
            var tiny = new TinyChunkBufferWriter(12 + pad);
            var w = new JsonWriter(tiny);
            w.WriteStartObject();
            w.WritePropertyName("ab"u8);
            w.WriteNumber(12345);
            w.WritePropertyName("cd"u8);
            w.WriteString("efgh"u8);
            w.WriteEndObject();
            await Assert.That(Str(tiny.WrittenSpan)).IsEqualTo("{\"ab\":12345,\"cd\":\"efgh\"}");
        }
    }

    [Test]
    public async Task LargeDocumentNeedsSeveralSpansAndIsByteIdentical()
    {
        var expected = WriteThrough(
            new ArrayBufferWriter<byte>(16),
            (WriterAction)((ref JsonWriter w) => WriteLargeDocument(ref w))
        );
        await Assert.That(expected.Length).IsGreaterThan(4096);

        foreach (var chunk in new[] { 1, 2, 3, 17, 255, 256, 257, 1024 })
        {
            var tiny = new TinyChunkBufferWriter(chunk);
            var actual = WriteThrough(
                tiny,
                (WriterAction)((ref JsonWriter w) => WriteLargeDocument(ref w))
            );
            await AssertBytes(expected, actual, $"large document at chunk size {chunk}");
            // More than one fetch and more than one commit: the cache really was used.
            await Assert.That(tiny.GetSpanCalls).IsGreaterThan(1);
            await Assert.That(tiny.AdvanceCalls).IsGreaterThan(1);
        }
    }

    [Test]
    public async Task OneFetchPerChunkInsteadOfPerToken()
    {
        // 300 ints in one array: the un-cached writer made two interface calls per token
        // (600+), the cached one makes them per span.
        var tiny = new TinyChunkBufferWriter(256);
        var w = new JsonWriter(tiny);
        w.WriteStartArray();
        for (var i = 0; i < 300; i++)
            w.WriteNumber(i);
        w.WriteEndArray();
        await Assert.That(tiny.GetSpanCalls).IsLessThan(20);
        await Assert.That(Str(tiny.WrittenSpan)).StartsWith("[0,1,2,");
    }

    // ── empty / single value / deep nesting / tail ───────────────────────────

    [Test]
    public async Task EmptyDocumentWritesNothing()
    {
        var buf = new ArrayBufferWriter<byte>(64);
        var w = new JsonWriter(buf);
        _ = w.Buffer;
        var fromArrayBuffer = (buf.WrittenCount, w.BytesWritten);

        var tiny = new TinyChunkBufferWriter(8);
        var w2 = new JsonWriter(tiny);
        _ = w2.Buffer;
        var fromTiny = (tiny.WrittenSpan.Length, tiny.AdvanceCalls);

        await Assert.That(fromArrayBuffer.WrittenCount).IsEqualTo(0);
        await Assert.That(fromArrayBuffer.BytesWritten).IsEqualTo(0);
        await Assert.That(fromTiny.Length).IsEqualTo(0);
        await Assert.That(fromTiny.AdvanceCalls).IsEqualTo(0);
    }

    [Test]
    public async Task SingleBareValueIsCommittedWithoutClosingAContainer()
    {
        // Depth 0 has nothing that will ever close, so the bytes must be on the wire right
        // after the call — the one rule a bare-value caller must be able to trust.
        var checkedAny = false;
        foreach (var (name, write, _) in Documents())
        {
            if (name is not ("null" or "true" or "bare int" or "bare string"))
                continue;
            checkedAny = true;
            var buf = new ArrayBufferWriter<byte>(8);
            var w = new JsonWriter(buf);
            write(ref w);
            // Read both before the first await: a ref struct cannot live across one.
            var written = buf.WrittenCount;
            var reported = (int)w.BytesWritten;
            await Assert.That(written).IsGreaterThan(0);
            await Assert.That(written).IsEqualTo(reported);
        }
        await Assert.That(checkedAny).IsTrue();
    }

    [Test]
    public async Task DeepNestingIsCompleteAtTheEnd()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        for (var i = 0; i < 40; i++)
            w.WriteStartArray();
        w.WriteNumber(1);
        for (var i = 0; i < 40; i++)
            w.WriteEndArray();
        await Assert
            .That(Str(buf.WrittenSpan))
            .IsEqualTo(new string('[', 40) + "1" + new string(']', 40));
    }

    [Test]
    public async Task BytesWrittenIsTheLengthOfTheOutput()
    {
        foreach (var (name, write, indented) in Documents())
        {
            var buf = new ArrayBufferWriter<byte>(8);
            var w = new JsonWriter(buf, indented: indented);
            write(ref w);
            _ = w.Buffer;
            await Assert.That(buf.WrittenCount).IsEqualTo((int)w.BytesWritten).Because(name);
        }
    }

    // ── the converter hand-off (Buffer getter) ───────────────────────────────

    [Test]
    public async Task BufferHandoffMidObjectCommitsTheTailAndKeepsOrder()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("inner"u8);
        // A converter / custom serializer sees only the raw buffer: the writer has to put
        // what it holds on the wire *before* handing it over, and pick up after it.
        var raw = w.Buffer;
        raw.Write("\"raw\""u8);
        w.NotifyRawValueWritten();
        w.WritePropertyName("n"u8);
        w.WriteNumber(7);
        w.WriteEndObject();
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("{\"inner\":\"raw\",\"n\":7}");
    }

    [Test]
    public async Task BufferHandoffInsideANestedConverterKeepsOrder()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        w.WriteNumber(1);
        w.WritePropertyName("deep"u8);
        // Nested writer on the very same buffer, the way the converters nest.
        var inner = new JsonWriter(w.Buffer);
        inner.WriteStartObject();
        inner.WritePropertyName("b"u8);
        inner.WriteNumber(2);
        inner.WriteEndObject();
        w.NotifyRawValueWritten();
        w.WritePropertyName("c"u8);
        w.WriteNumber(3);
        w.WriteEndObject();
        await Assert.That(Str(buf.WrittenSpan)).IsEqualTo("{\"a\":1,\"deep\":{\"b\":2},\"c\":3}");
    }

    [Test]
    public async Task FlushThenWriteAgainAppendsInsteadOfOverwriting()
    {
        // The 4b lesson: a Flush that only rewinds the cursor (without dropping the span)
        // makes the next write land in memory that has already been committed.
        var (mid, final) = FlushThenWriteAgain();
        await Assert.That(mid).IsEqualTo("{\"a\":");
        await Assert.That(final).IsEqualTo("{\"a\":1}");
    }

    private static (string Mid, string Final) FlushThenWriteAgain()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        _ = w.Buffer;
        var mid = Str(buf.WrittenSpan);
        w.WriteNumber(1);
        w.WriteEndObject();
        return (mid, Str(buf.WrittenSpan));
    }

    // ── copy semantics ───────────────────────────────────────────────────────

    /// <summary>Takes the writer BY VALUE — the mistake a <c>ref struct</c> lets you write.</summary>
    private static void WriteNumberByValue(JsonWriter copy) => copy.WriteNumber(42); // BY-VALUE-ALLOWED: the counterexample

    private static void WriteNumberByRef(ref JsonWriter w) => w.WriteNumber(42);

    private static (string ByValue, string ByRef) ArrayElementByValueOrRef()
    {
        var byValue = new ArrayBufferWriter<byte>(8);
        var wv = new JsonWriter(byValue);
        wv.WriteStartArray();
        WriteNumberByValue(wv);
        wv.WriteEndArray();

        var byRef = new ArrayBufferWriter<byte>(8);
        var wr = new JsonWriter(byRef);
        wr.WriteStartArray();
        WriteNumberByRef(ref wr);
        wr.WriteEndArray();
        return (Str(byValue.WrittenSpan), Str(byRef.WrittenSpan));
    }

    private static string ObjectMemberByValue()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        WriteNumberByValue(w);
        w.WriteEndObject(); // the caller's writer still believes a property name is pending
        return Str(buf.WrittenSpan);
    }

    private static string ObjectMemberByRef()
    {
        var buf = new ArrayBufferWriter<byte>(8);
        var w = new JsonWriter(buf);
        w.WriteStartObject();
        w.WritePropertyName("a"u8);
        WriteNumberByRef(ref w);
        w.WriteEndObject();
        return Str(buf.WrittenSpan);
    }

    [Test]
    public async Task PassingTheWriterByValueLosesTheValueWrittenThroughTheCopy()
    {
        // The span cursor is a *value* field: a copy writes into the same memory but its
        // flush never runs (depth 1 never calls CommitBare), so the value is silently
        // dropped and the closing brace lands on top of it. This is why every helper in this
        // repository takes `ref JsonWriter`, and why the generator emits
        // `Serialize(ref JsonWriter jw, ...)`.
        // As an array element the loss is silent: the copy's "42" is dropped and the
        // closing bracket lands on top of it, so "[]" comes out instead of "[42]".
        var (byValueSilent, byRefArray) = ArrayElementByValueOrRef();
        await Assert.That(byValueSilent).IsEqualTo("[]");
        await Assert.That(byRefArray).IsEqualTo("[42]");

        // After a property name the existing guard catches it, but for the wrong reason:
        // the copy cleared the pending-name flag, not the caller's writer.
        var threw = false;
        try
        {
            _ = ObjectMemberByValue();
        }
        catch (FormatException)
        {
            threw = true;
        }
        await Assert.That(threw).IsTrue();
        var (byRefObject, _) = (ObjectMemberByRef(), true);
        await Assert.That(byRefObject).IsEqualTo("{\"a\":42}");
    }

    [Test]
    public async Task NoSourceFilePassesTheWriterByValue()
    {
        // A static check, because a by-value call site compiles and then corrupts the
        // output. Covers the whole repository: library, generator, samples and every test.
        var root = FindRepositoryRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
            )
                continue;
            scanned++;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("JsonWriter", StringComparison.Ordinal))
                    continue;
                var trimmed = line.TrimStart();
                if (
                    line.Contains("ref JsonWriter", StringComparison.Ordinal)
                    || line.Contains("in JsonWriter", StringComparison.Ordinal)
                    || line.Contains("out JsonWriter", StringComparison.Ordinal)
                    || trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)
                    || line.Contains("ref struct JsonWriter", StringComparison.Ordinal)
                    || line.Contains("cref=\"JsonWriter", StringComparison.Ordinal)
                    // The one deliberate counterexample, pinned by the test above.
                    || line.Contains("BY-VALUE-ALLOWED", StringComparison.Ordinal)
                )
                    continue;
                // A parameter-shaped occurrence of the type without ref/in/out.
                if (
                    System.Text.RegularExpressions.Regex.IsMatch(
                        line,
                        @"\bJsonWriter\s+[A-Za-z_]\w*\s*[,)]"
                    )
                )
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
            }
        }
        await Assert.That(scanned).IsGreaterThan(100);
        await Assert
            .That(string.Join(Environment.NewLine, offenders))
            .IsEqualTo("")
            .Because("no source file may take a JsonWriter by value");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PicoSerDe.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not find PicoSerDe.slnx above '{AppContext.BaseDirectory}'."
        );
    }
}
