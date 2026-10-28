namespace PicoJetson.Tests;

/// <summary>
/// The property-name write: <c>"name":</c> — separator, quotes and colon — is staged as one
/// write behind one <c>Ensure</c> instead of four writes each paying its own bound check.
/// </summary>
/// <remarks>
/// <para>
/// Nothing about the emitted bytes is allowed to move, so this file is an equivalence proof,
/// not a behaviour test: every document below is compared against a literal written out by
/// hand, and the escaping arm is compared against the escape path the writer has always had.
/// The three ways the fused write can be wrong are all covered:
/// </para>
/// <list type="bullet">
///   <item>the separator: a missing comma, a doubled one, or one written for the first member
///   of an object (the staging happens before <c>_needsComma</c> is committed, so the "is a
///   comma needed" decision has exactly one owner);</item>
///   <item>the byte count: one byte short (the colon dropped) or one too many (the quote
///   doubled) changes the document, and <c>BytesWritten</c> would then disagree with the
///   buffer;</item>
///   <item>the escape arm: a name that needs escaping must not be half-staged before the
///   writer gives up and takes the general path.</item>
/// </list>
/// <para>
/// Names that need escaping are the interesting half here, because they are decided at run
/// time by the scan and the whole document then depends on the fallback being verbatim: quotes,
/// backslashes, TAB, U+0001, non-ASCII, emoji, and a name long enough to cross the vectorized
/// scan (16+ bytes) and the <c>MinSpanSize</c> line (256+ bytes).
/// </para>
/// </remarks>
public class JsonWriterPropertyNameTests
{
    private delegate void WriterAction(ref JsonWriter w);

    private static byte[] Through(IBufferWriter<byte> buffer, WriterAction write, bool indented) =>
        Through(buffer, write, indented, out _);

    private static byte[] Through(
        IBufferWriter<byte> buffer,
        WriterAction write,
        bool indented,
        out long bytesWritten
    )
    {
        var w = new JsonWriter(buffer, indented: indented);
        write(ref w);
        bytesWritten = w.BytesWritten;
        return buffer switch
        {
            ArrayBufferWriter<byte> ab => ab.WrittenSpan.ToArray(),
            TinyChunkBufferWriter tiny => tiny.WrittenSpan.ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(buffer)),
        };
    }

    private static string Write(WriterAction write, bool indented = false) =>
        Encoding.UTF8.GetString(Through(new ArrayBufferWriter<byte>(8), write, indented));

    /// <summary>Writes one property per name, in order, each with a distinct int value.</summary>
    private static WriterAction ObjectWith(params byte[][] names) =>
        (ref JsonWriter w) =>
        {
            w.WriteStartObject();
            for (var i = 0; i < names.Length; i++)
            {
                w.WritePropertyName(names[i]);
                w.WriteNumber(i);
            }
            w.WriteEndObject();
        };

    private static byte[] Name(string s) => Encoding.UTF8.GetBytes(s);

    // ── 1. the plain names: separator, quotes and colon in one staged write ──

    [Test]
    public async Task PlainNames_AreWrittenWithCommasBetweenAndNoneBeforeTheFirst()
    {
        await Assert
            .That(Write(ObjectWith(Name("Name"), Name("Age"))))
            .IsEqualTo("""{"Name":0,"Age":1}""");
    }

    [Test]
    public async Task SingleName_HasNoSeparatorAtAll()
    {
        await Assert.That(Write(ObjectWith(Name("F01")))).IsEqualTo("""{"F01":0}""");
    }

    [Test]
    public async Task EmptyObject_WritesNoNameAndNoSeparator()
    {
        await Assert.That(Write(ObjectWith())).IsEqualTo("{}");
    }

    [Test]
    public async Task EmptyName_IsWrittenAsAnEmptyQuotedName()
    {
        // "" is a legal JSON member name and needs no escaping: the fused path must write
        // it as `"":` rather than skip the token.
        await Assert.That(Write(ObjectWith(Name("")))).IsEqualTo("""{"":0}""");
    }

    [Test]
    public async Task ValuesOtherThanNumbers_KeepTheirSeparator()
    {
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("s"u8);
                w.WriteString("x"u8);
                w.WritePropertyName("o"u8);
                w.WriteStartObject();
                w.WritePropertyName("i"u8);
                w.WriteNumber(1);
                w.WriteEndObject();
                w.WritePropertyName("a"u8);
                w.WriteStartArray();
                w.WriteBoolean(true);
                w.WriteNull();
                w.WriteEndArray();
                w.WriteEndObject();
            }
        );

        await Assert.That(json).IsEqualTo("""{"s":"x","o":{"i":1},"a":[true,null]}""");
    }

    [Test]
    public async Task IndentedWriter_KeepsItsColonSpaceAndIndentation()
    {
        // The indented colon is `": "` and the fused path does not own it: an indented writer
        // stays on the general path, so the trailing space cannot be lost.
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("a"u8);
                w.WriteNumber(1);
                w.WritePropertyName("b"u8);
                w.WriteNumber(2);
                w.WriteEndObject();
            },
            indented: true
        );

        await Assert.That(json).IsEqualTo("{\n  \"a\": 1,\n  \"b\": 2\n}");
    }

    [Test]
    public async Task ZeroByteName_IsWrittenAndEscaped()
    {
        // "\0" is not plain: it is a control byte and must reach the escaping path.
        await Assert.That(Write(ObjectWith([0x00]))).IsEqualTo("""{"\u0000":0}""");
    }

    // ── 2. the escape arm: the fallback must be verbatim, and must not double a comma ──

    [Test]
    public async Task EscapableNames_TakeTheGeneralPath_AndKeepTheirSeparators()
    {
        var json = Write(
            ObjectWith(
                Name("plain"),
                Name("quo\"te"),
                Name("back\\slash"),
                Name("tab\there"),
                Name("ctl\u0001"),
                Name("非 ASCII 漢字"),
                Name("emoji 😀🎉"),
                Name("plain again")
            )
        );

        // The escaping table, not this change, decides the escapes: `"` → `\"`,
        // `\` → `\\`, TAB → `\t`, U+0001 → `\u0001`, everything ≥ 0x20 raw UTF-8.
        await Assert
            .That(json)
            .IsEqualTo(
                """{"plain":0,"quo\"te":1,"back\\slash":2,"tab\there":3,"ctl\u0001":4,"非 ASCII 漢字":5,"emoji 😀🎉":6,"plain again":7}"""
            );
    }

    [Test]
    public async Task EscapableFirstMember_WritesTheCommaExactlyOnce()
    {
        // Regression shape: the staging sets the comma before the escape decision, so a
        // fallback that re-asked "is a comma needed?" would emit `{,"quo\"te":0`.
        await Assert
            .That(Write(ObjectWith(Name("quo\"te"), Name("plain"))))
            .IsEqualTo("""{"quo\"te":0,"plain":1}""");
    }

    [Test]
    public async Task LongNames_CrossTheVectorizedScanAndTheCacheLine()
    {
        // 15 / 16 / 17 cross the vector width; 255 / 256 / 257 cross MinSpanSize; the escape
        // sits at both ends so neither scan can be skipped by an early exit.
        foreach (var length in new[] { 15, 16, 17, 255, 256, 257, 1024 })
        {
            var plain = new string('n', length);
            var json = Write(
                ObjectWith(Encoding.UTF8.GetBytes(plain), Encoding.UTF8.GetBytes("x"))
            );
            await Assert
                .That(json)
                .IsEqualTo($$"""{"{{plain}}":0,"x":1}""")
                .Because($"a {length}-byte name must be written verbatim");

            var escaped = "\"" + new string('n', length - 2) + "\"";
            var escapedJson = Write(ObjectWith(Encoding.UTF8.GetBytes(escaped)));
            await Assert
                .That(escapedJson)
                .IsEqualTo($$"""{"\"{{new string('n', length - 2)}}\"":0}""")
                .Because($"a {length}-byte name with quotes must be escaped");
        }
    }

    [Test]
    public async Task NonAsciiNameAboveTheVectorWidth_IsNotEscaped()
    {
        // Non-ASCII is not escapable in JSON, so a long CJK name must reach the fused path
        // *and* be written as raw UTF-8.
        var cjk = string.Concat(Enumerable.Repeat("汉", 16));
        await Assert
            .That(Write(ObjectWith(Encoding.UTF8.GetBytes(cjk))))
            .IsEqualTo($"{{\"{cjk}\":0}}");
    }

    [Test]
    public async Task CharOverload_ReachesTheSameFusedPath()
    {
        // The char overload transcodes and delegates; the document must match the byte form.
        var json = Write(
            (ref JsonWriter w) =>
            {
                w.WriteStartObject();
                w.WritePropertyName("plain".AsSpan());
                w.WriteNumber(0);
                w.WritePropertyName("quo\"te".AsSpan());
                w.WriteNumber(1);
                w.WriteEndObject();
            }
        );

        await Assert.That(json).IsEqualTo("""{"plain":0,"quo\"te":1}""");
    }

    // ── 3. buffer boundaries: one, two and three bytes short ──

    [Test]
    public async Task ChunkSizesAroundTheNameAreAllByteIdentical()
    {
        // A name is staged as n + 3 bytes (quote, name, quote, colon) and preceded by an
        // optional 1-byte comma. The chunk is sized so the token starts with exactly 1, 2 or
        // 3 bytes left, i.e. the writer must refetch instead of spilling.
        var write = ObjectWith(Name("abcd"), Name("ef"), Name("quo\"te"), Name("g"));
        var expected = Through(new ArrayBufferWriter<byte>(8), write, false);

        for (var chunk = 1; chunk <= 40; chunk++)
        {
            var tiny = new TinyChunkBufferWriter(chunk);
            var actual = Through(tiny, write, false);
            await Assert
                .That(Encoding.UTF8.GetString(actual))
                .IsEqualTo(Encoding.UTF8.GetString(expected))
                .Because($"chunk size {chunk} must not change the bytes");
        }
    }

    [Test]
    public async Task ChunkEndingOneTwoThreeBytesShortOfThePropertyName()
    {
        // `{` is 1 byte; each subsequent member is `,"name":value`. Padding the chunk by
        // 0..5 shifts the boundary across every byte of `"ab":` in turn.
        for (var pad = 0; pad < 6; pad++)
        {
            var tiny = new TinyChunkBufferWriter(9 + pad);
            var json = Encoding.UTF8.GetString(
                Through(
                    tiny,
                    (ref JsonWriter w) =>
                    {
                        w.WriteStartObject();
                        w.WritePropertyName("ab"u8);
                        w.WriteNumber(1);
                        w.WritePropertyName("cd"u8);
                        w.WriteNumber(23);
                        w.WriteEndObject();
                    },
                    false
                )
            );
            await Assert.That(json).IsEqualTo("""{"ab":1,"cd":23}""").Because($"pad {pad}");
        }
    }

    [Test]
    public async Task BytesWrittenMatchesTheBuffer_ForEveryDocumentHere()
    {
        foreach (
            var (name, write, indented) in new (string, WriterAction, bool)[]
            {
                ("plain", ObjectWith(Name("Name"), Name("Age")), false),
                (
                    "escapes",
                    ObjectWith(Name("q\"x"), Name("b\\x"), Name("t\tx"), Name("汉")),
                    false
                ),
                ("indented", ObjectWith(Name("a"), Name("b")), true),
            }
        )
        {
            var bytes = Through(
                new ArrayBufferWriter<byte>(8),
                write,
                indented,
                out var bytesWritten
            );
            await Assert
                .That(bytesWritten)
                .IsEqualTo(bytes.Length)
                .Because($"BytesWritten must count every byte of '{name}'");
        }
    }

    // ── 4. staging: no allocation, and one fetch per chunk rather than per token ──

    private static void Run(ArrayBufferWriter<byte> buffer)
    {
        buffer.Clear();
        var w = new JsonWriter(buffer);
        w.WriteStartObject();
        for (var i = 0; i < 200; i++)
        {
            w.WritePropertyName("field"u8);
            w.WriteNumber(i);
        }
        w.WriteEndObject();
    }

    [Test]
    public async Task PropertyNameWritesAllocateNothing()
    {
        // The buffer is created once, outside the measured window: it is the only thing here
        // that is allowed to allocate, and it is not part of the change.
        var buffer = new ArrayBufferWriter<byte>(4096);
        Run(buffer); // warm up: the first call pays for any JIT-side allocation
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
            Run(buffer);
        var perRun = (GC.GetAllocatedBytesForCurrentThread() - before) / 50.0;

        await Assert
            .That(perRun)
            .IsEqualTo(0.0)
            .Because("staging a property name allocates nothing");
    }

    [Test]
    public async Task OneFetchPerChunk_NotPerNameToken()
    {
        // 300 names: four writes each would have been 1200 chances to refetch; with the token
        // staged in one go and a 256-byte cache slot the count stays in single digits.
        var tiny = new TinyChunkBufferWriter(256);
        var w = new JsonWriter(tiny);
        w.WriteStartObject();
        for (var i = 0; i < 300; i++)
        {
            w.WritePropertyName("ab"u8);
            w.WriteNumber(i);
        }
        w.WriteEndObject();

        await Assert.That(tiny.GetSpanCalls).IsLessThan(60);
        await Assert
            .That(Encoding.UTF8.GetString(tiny.WrittenSpan))
            .StartsWith("""{"ab":0,"ab":1,""");
    }

    [Test]
    public async Task ClosingAnObjectWithAPendingNameStillThrows()
    {
        // The name write is bookkeeping as well as bytes: `_afterPropertyName` must still be
        // set, so an object closed with no value keeps failing.
        var buf = new ArrayBufferWriter<byte>(8);
        var threw = false;
        try
        {
            var w = new JsonWriter(buf);
            w.WriteStartObject();
            w.WritePropertyName("orphan"u8);
            w.WriteEndObject();
        }
        catch (FormatException)
        {
            threw = true;
        }

        await Assert
            .That(threw)
            .IsTrue()
            .Because("a name with no value must still be rejected when the object closes");
    }
}
