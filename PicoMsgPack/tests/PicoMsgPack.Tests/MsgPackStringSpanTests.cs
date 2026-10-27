namespace PicoMsgPack.Tests;

/// <summary>
/// Task 4c guard rails. The generated serializers used to encode every string twice —
/// once into a freshly allocated <c>byte[]</c> (<c>Encoding.UTF8.GetBytes(value)</c>) and
/// once more into the writer's span — so a 15-string object allocated 600 B/op. Strings
/// now reach the writer as characters and are encoded straight into the span it owns.
/// <para>
/// Two things must stay true, and neither is visible to a round-trip test: the byte
/// stream must not move (goldens below are the <em>pre-change</em> output, captured from
/// the build before this change; raw capture in
/// <c>.superpowers/sdd/perf-mp-str/logs/capture-pre.txt</c>), and the writer path must
/// stop allocating. The header length is derived from the UTF-8 <em>byte</em> count, not
/// the char count, so the goldens deliberately walk every header regime with strings
/// whose char and byte lengths disagree.
/// </para>
/// </summary>
public class MsgPackStringSpanTests
{
    // ── Goldens: output of the pre-change serializers (byte[]-per-string) ────

    /// <summary>15 ints + 15 strings, 333 B — the shape that allocated 600 B/op.</summary>
    private const string FifteenHex =
        "DE001E00D103E801D103E902D103EA03D103EB04D103EC05D103ED06D103EE07D103EF08D103F009D103F10AD103F20B"
        + "D103F30CD103F40DD103F50ED103F60FB076616C75652D30302D7061796C6F616410B076616C75652D30312D7061796C"
        + "6F616411B076616C75652D30322D7061796C6F616412B076616C75652D30332D7061796C6F616413B076616C75652D30"
        + "342D7061796C6F616414B076616C75652D30352D7061796C6F616415B076616C75652D30362D7061796C6F616416B076"
        + "616C75652D30372D7061796C6F616417B076616C75652D30382D7061796C6F616418B076616C75652D30392D7061796C"
        + "6F616419B076616C75652D31302D7061796C6F61641AB076616C75652D31312D7061796C6F61641BB076616C75652D31"
        + "322D7061796C6F61641CB076616C75652D31332D7061796C6F61641DB076616C75652D31342D7061796C6F6164";

    /// <summary>Collections: sequence elements + dictionary keys/values.</summary>
    private const string CollectionHex =
        "830096A0A161A668C3A96C6C6FA6E4B8ADE69687A4F09F9880D9286D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D"
        + "6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D6D0183A26B31A0A26B32A9E4B8ADE69687E580BCA46BC3A979A576616C75"
        + "650293A161C0A6E4B8ADE69687";

    /// <summary>1555 B of header boundaries and awkward character classes — too long to keep as hex.</summary>
    private const int EdgeLength = 1555;

    private const string EdgeSha256Hex =
        "5893E4840CE51785DE63C362D4ABD67F6146F533BFA2192411E0EB3614F26291";

    /// <summary>Pre-change allocation on the writer path (reused <see cref="ArrayBufferWriter{T}"/>).</summary>
    private const double PreChangeFifteenBytesPerOp = 600.0;

    private static readonly int[] Slacks = [0, 1, 2, 3, 7];

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

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

    private static async Task ExpectHex(string expectedHex, byte[] actual, string what)
    {
        var expected = Convert.FromHexString(expectedHex);
        await Assert.That(Diff(expected, actual)).IsEqualTo("identical").Because(what);
    }

    // ── 1. The wire did not move ────────────────────────────────────────────

    [Test]
    public async Task Serialize_MatchesThePreChangeGolden()
    {
        await ExpectHex(
            FifteenHex,
            MsgPackSerializer.SerializeToUtf8Bytes(StrFifteenModel.Create()),
            "15 strings"
        );
        await ExpectHex(
            CollectionHex,
            MsgPackSerializer.SerializeToUtf8Bytes(StrCollectionModel.Create()),
            "list elements + dictionary keys/values"
        );

        var edge = MsgPackSerializer.SerializeToUtf8Bytes(new StrEdgeModel());
        await Assert.That(edge.Length).IsEqualTo(EdgeLength);
        await Assert
            .That(Hex(System.Security.Cryptography.SHA256.HashData(edge)))
            .IsEqualTo(EdgeSha256Hex)
            .Because("header regimes and character classes");
    }

    /// <summary>
    /// Forces the span to run out on almost every token: the buffer hands out exactly the
    /// size the encoder asked for (plus <paramref name="slack"/>), so a string of 600 bytes
    /// starts near the end of a span and must cross into a fresh one mid-payload. A header
    /// computed from the char count, a mis-advanced cursor, or an <c>Ensure</c> that does
    /// not cover header + payload changes the bytes here and nowhere else.
    /// </summary>
    [Test]
    public async Task Serialize_IntoExactSizedSpans_IsByteIdentical()
    {
        foreach (var slack in Slacks)
        {
            await ExpectHex(
                FifteenHex,
                Serialize(StrFifteenModel.Create(), slack),
                $"15 strings slack={slack}"
            );
            await ExpectHex(
                CollectionHex,
                Serialize(StrCollectionModel.Create(), slack),
                $"collections slack={slack}"
            );

            var edge = Serialize(new StrEdgeModel(), slack);
            await Assert.That(edge.Length).IsEqualTo(EdgeLength).Because($"edge slack={slack}");
            await Assert
                .That(Hex(System.Security.Cryptography.SHA256.HashData(edge)))
                .IsEqualTo(EdgeSha256Hex)
                .Because($"edge slack={slack}");
        }
    }

    private static byte[] Serialize<T>(T value, int slack)
        where T : allows ref struct
    {
        var writer = new ExactSizedBufferWriter(slack);
        MsgPackSerializer.Serialize(writer, value);
        return writer.WrittenSpan.ToArray();
    }

    // ── 2. The allocation is gone ───────────────────────────────────────────

    /// <summary>
    /// Writes the 15-string model through a <em>reused</em> buffer, so the only thing being
    /// counted is what the serializer itself allocates. Before this change: 600 B/op
    /// (15 × one <c>byte[]</c>, 40 B each once headers are included). After: nothing.
    /// </summary>
    [Test]
    public async Task WriterPath_AllocatesNothing()
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        var fifteen = StrFifteenModel.Create();
        var edge = new StrEdgeModel();
        var collection = StrCollectionModel.Create();

        // Warm up so the measurement does not include JIT/tiering allocations.
        for (int i = 0; i < 200; i++)
        {
            buffer.Clear();
            MsgPackSerializer.Serialize(buffer, fifteen);
        }

        double bytesPerOp = Measure(() =>
        {
            buffer.Clear();
            MsgPackSerializer.Serialize(buffer, fifteen);
        });
        await Assert
            .That(bytesPerOp)
            .IsEqualTo(0.0)
            .Because(
                $"15-string object allocated {bytesPerOp} B/op (was {PreChangeFifteenBytesPerOp})"
            );

        double edgePerOp = Measure(() =>
        {
            buffer.Clear();
            MsgPackSerializer.Serialize(buffer, edge);
        });
        await Assert.That(edgePerOp).IsEqualTo(0.0).Because("edge-character object");

        double collectionPerOp = Measure(() =>
        {
            buffer.Clear();
            MsgPackSerializer.Serialize(buffer, collection);
        });
        await Assert.That(collectionPerOp).IsEqualTo(0.0).Because("list elements + dictionary");
    }

    /// <summary>Bytes allocated per call, over <c>iters</c> calls, on this thread only.</summary>
    private static double Measure(Action body, int iters = 1000)
    {
        for (int i = 0; i < 200; i++)
            body();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < iters; i++)
            body();
        long after = GC.GetAllocatedBytesForCurrentThread();
        return (after - before) / (double)iters;
    }

    // ── 3. The two paths agree, byte for byte ───────────────────────────────

    /// <summary>
    /// The old call shape and the new one, side by side on the same writer: every case
    /// must produce the same bytes. The cases are chosen so that a header derived from the
    /// char count instead of the byte count, a lost payload, or an off-by-one cursor shows
    /// up: 0/1/2/3/4-byte characters, NUL runs, every header boundary (31/32, 255/256,
    /// 65535/65536), a lone surrogate (replacement character) and a 66 KB payload.
    /// </summary>
    [Test]
    public async Task CharOverload_IsByteIdenticalToTheByteArrayPath()
    {
        foreach (var (label, text) in StringCases())
        {
            var oldBytes = WriteViaByteArray(text);
            var newBytes = WriteViaChars(text);
            await Assert.That(Diff(oldBytes, newBytes)).IsEqualTo("identical").Because(label);

            // The header is a function of the UTF-8 byte count; assert it explicitly so a
            // wrong regime fails loudly instead of "both paths are equally wrong".
            int byteCount = Encoding.UTF8.GetByteCount(text);
            await Assert
                .That(HeaderLength(newBytes))
                .IsEqualTo(ExpectedHeaderLength(byteCount))
                .Because($"{label} header (byteCount {byteCount})");
            await Assert
                .That(newBytes.Length)
                .IsEqualTo(ExpectedHeaderLength(byteCount) + byteCount)
                .Because($"{label} total length");
        }
    }

    /// <summary>
    /// The same two paths through a buffer that hands out only what was asked for, so the
    /// 600-byte and 66 KB strings have to cross span boundaries mid-payload.
    /// </summary>
    [Test]
    public async Task CharOverload_AcrossSpanBoundaries_IsByteIdentical()
    {
        foreach (var slack in Slacks)
        {
            foreach (var (label, text) in StringCases())
            {
                var oldBytes = WriteViaByteArray(text, slack);
                var newBytes = WriteViaChars(text, slack);
                await Assert
                    .That(Diff(oldBytes, newBytes))
                    .IsEqualTo("identical")
                    .Because($"{label} slack={slack}");
            }
        }
    }

    /// <summary>
    /// A short multi-byte string written immediately after a string that consumed a whole
    /// span. The writer prefixes a header, so "room for the payload" is not "room for the
    /// message": with <c>slack = 7</c> the span has seven bytes left, which fits a 4-char
    /// request but not the 12 bytes those four CJK characters encode to. An <c>Ensure</c>
    /// that counts characters instead of worst-case bytes overruns the span here and
    /// nowhere else.
    /// </summary>
    [Test]
    public async Task ShortMultiByteString_RightAfterAFullSpan_IsByteIdentical()
    {
        (string Label, string Pre, string Post)[] pairs =
        [
            ("600 then 4 CJK (12 B)", new string('q', 600), "中文测试"),
            ("300 then 3 CJK (9 B)", new string('r', 300), "中文字"),
            ("1000 then 10 mixed", new string('s', 1000), "abc中文😀中文"),
            ("256 then emoji pair", new string('t', 256), "😀😀"),
        ];

        foreach (var slack in Slacks)
        foreach (var (label, pre, post) in pairs)
        {
            var oldBytes = WritePairViaByteArray(pre, post, slack);
            var newBytes = WritePairViaChars(pre, post, slack);
            await Assert
                .That(Diff(oldBytes, newBytes))
                .IsEqualTo("identical")
                .Because($"{label} slack={slack}");
        }
    }

    private static byte[] WritePairViaByteArray(string pre, string post, int slack)
    {
        var buffer = MakeBuffer(slack);
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartArray(2);
        mw.WriteString(Encoding.UTF8.GetBytes(pre));
        mw.WriteString(Encoding.UTF8.GetBytes(post));
        mw.WriteEndArray();
        return Committed(buffer);
    }

    private static byte[] WritePairViaChars(string pre, string post, int slack)
    {
        var buffer = MakeBuffer(slack);
        var mw = new MsgPackWriter(buffer);
        mw.WriteStartArray(2);
        mw.WriteString(pre);
        mw.WriteString(post);
        mw.WriteEndArray();
        return Committed(buffer);
    }

    /// <summary>Writing characters advances the cursor and the byte counters by the bytes written.</summary>
    [Test]
    public async Task CharOverload_BytesWrittenMatchesTheCommittedLength()
    {
        foreach (var slack in Slacks)
        {
            var buffer = new ExactSizedBufferWriter(slack);
            var mw = new MsgPackWriter(buffer);
            mw.WriteStartArray(3);
            mw.WriteString("中文😀");
            mw.WriteString("");
            mw.WriteString(new string('q', 600));
            mw.WriteEndArray();

            await Assert
                .That(mw.BytesWritten)
                .IsEqualTo(buffer.WrittenCount)
                .Because($"slack={slack}");
        }
    }

    /// <summary>Arguments are (label, text); the label is what a failure names.</summary>
    private static IEnumerable<(string Label, string Text)> StringCases()
    {
        yield return ("empty", "");
        yield return ("single ascii", "a");
        yield return ("nul char", "\0");
        yield return ("nul run", "a\0b\0\0");
        yield return ("1-2 byte boundary", "\u007F\u0080");
        yield return ("2-3 byte boundary", "\u07FF\u0800\uFFFF");
        yield return ("latin2", "héllo");
        yield return ("cjk3", "中文测试");
        yield return ("emoji4", "😀🎉");
        yield return ("mixed", "abc中文😀\0");
        yield return ("lone high surrogate", "\uD800");
        yield return ("lone low surrogate", "\uDC00");
        yield return ("surrogate pair split by a nul", "\uD83D\0\uDE00");
        yield return ("31 ascii (fixstr max)", new string('x', 31));
        yield return ("32 ascii (str8 min)", new string('y', 32));
        yield return ("255 ascii (str8 max)", new string('z', 255));
        yield return ("256 ascii (str16 min)", new string('w', 256));
        yield return ("31 cjk (93 bytes, str8)", new string('中', 31));
        yield return ("11 cjk (33 bytes, str8 after a one-byte move)", new string('中', 11));
        yield return ("31 latin (62 bytes, str8 after a one-byte move)", new string('é', 31));
        yield return ("100 cjk (300 bytes, str16)", new string('中', 100));
        yield return ("600 ascii", new string('q', 600));
        yield return ("65535 bytes (str16 max)", new string('p', 65535));
        yield return ("65536 bytes (str32 min)", new string('o', 65536));
        yield return ("22000 cjk (66000 bytes, str32)", new string('大', 22000));
    }

    private static byte[] WriteViaByteArray(string text, int slack = -1)
    {
        // Exactly what the generator emitted before this change.
        var buffer = MakeBuffer(slack);
        var mw = new MsgPackWriter(buffer);
        mw.WriteString(Encoding.UTF8.GetBytes(text));
        _ = mw.Buffer;
        return Committed(buffer);
    }

    private static byte[] WriteViaChars(string text, int slack = -1)
    {
        var buffer = MakeBuffer(slack);
        var mw = new MsgPackWriter(buffer);
        mw.WriteString(text);
        _ = mw.Buffer;
        return Committed(buffer);
    }

    private static IBufferWriter<byte> MakeBuffer(int slack) =>
        slack < 0 ? new ArrayBufferWriter<byte>(256) : new ExactSizedBufferWriter(slack);

    /// <summary>Snapshot of what a writer actually published, for either buffer kind.</summary>
    private static byte[] Committed(IBufferWriter<byte> buffer) =>
        buffer switch
        {
            ArrayBufferWriter<byte> abw => abw.WrittenSpan.ToArray(),
            ExactSizedBufferWriter exact => exact.WrittenSpan.ToArray(),
            _ => throw new InvalidOperationException($"unexpected buffer {buffer.GetType().Name}"),
        };

    private static int HeaderLength(byte[] bytes) =>
        bytes[0] switch
        {
            >= 0xA0 and <= 0xBF => 1,
            0xD9 => 2,
            0xDA => 3,
            0xDB => 5,
            _ => -1,
        };

    private static int ExpectedHeaderLength(int byteCount) =>
        byteCount <= 31 ? 1
        : byteCount <= 255 ? 2
        : byteCount <= 65535 ? 3
        : 5;

    /// <summary>
    /// The one observable difference this change makes, pinned so it cannot happen silently.
    /// A <c>string?</c> member and a <c>List<string?></c> element are guarded by the generated
    /// code and still write <c>nil</c>. An <em>unguarded</em> position — a <c>List&lt;string&gt;</c>
    /// element or a <c>Dictionary&lt;string,string&gt;</c> value that is null through a
    /// nullability violation — used to reach <c>Encoding.UTF8.GetBytes(null)</c> and throw
    /// <see cref="ArgumentNullException"/>; a character span cannot be null, so it now writes
    /// an empty string. Nothing reachable from correctly annotated code changes.
    /// </summary>
    [Test]
    public async Task NullInAnUnguardedStringPosition_WritesAnEmptyString()
    {
        var unguarded = MsgPackSerializer.SerializeToUtf8Bytes(
            new StrCollectionModel
            {
                Items = [null!, "x"],
                Map = new(),
                Nullables = [],
            }
        );
        // 83 (3 members) | 00 | 92 A0 A1 78  (array: fixstr0, "x") | 01 | 80 | 02 | 90
        await ExpectHex("830092A0A17801800290", unguarded, "List<string> null element");

        var guarded = MsgPackSerializer.SerializeToUtf8Bytes(
            new StrCollectionModel
            {
                Items = [],
                Map = new(),
                Nullables = [null!, "y"],
            }
        );
        // 83 | 00 | 90 | 01 | 80 | 02 | 92 C0 A1 79  — nil, not ""
        await ExpectHex("83009001800292C0A179", guarded, "List<string?> null element");
    }

    // ── 4. Boundaries ───────────────────────────────────────────────────────

    [Test]
    public async Task EdgeStrings_RoundTrip()
    {
        var model = new StrEdgeModel();
        var back = MsgPackSerializer.Deserialize<StrEdgeModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Empty).IsEqualTo("");
        await Assert.That(back.OneByte).IsEqualTo("a");
        await Assert.That(back.Latin2).IsEqualTo("héllo");
        await Assert.That(back.Cjk3).IsEqualTo("中文测试");
        await Assert.That(back.Emoji4).IsEqualTo("😀🎉");
        await Assert.That(back.NullChars).IsEqualTo("a\0b\0\0");
        await Assert.That(back.L255).IsEqualTo(model.L255);
        await Assert.That(back.L256).IsEqualTo(model.L256);
        await Assert.That(back.Cjk100).IsEqualTo(model.Cjk100);
        await Assert.That(back.Long600).IsEqualTo(model.Long600);
        await Assert.That(back.Mixed).IsEqualTo(model.Mixed);
    }

    [Test]
    public async Task CollectionStrings_RoundTrip_IncludingNullElement()
    {
        var model = StrCollectionModel.Create();
        var back = MsgPackSerializer.Deserialize<StrCollectionModel>(
            MsgPackSerializer.SerializeToUtf8Bytes(model)
        );
        await Assert.That(back!.Items).IsEquivalentTo(model.Items);
        await Assert.That(back.Map["k2"]).IsEqualTo("中文值");
        await Assert.That(back.Map["kéy"]).IsEqualTo("value");
        await Assert.That(back.Nullables).IsEquivalentTo(model.Nullables);
    }

    /// <summary>
    /// <see cref="IBufferWriter{T}"/> that hands out exactly the requested size plus
    /// <paramref name="slack"/>; <c>Advance</c> rejects a count larger than what was handed
    /// out, turning a cursor bug into a failure instead of silent corruption.
    /// </summary>
    private sealed class ExactSizedBufferWriter : IBufferWriter<byte>
    {
        private readonly int _slack;
        private byte[] _buffer = new byte[32];
        private int _pos;
        private int _handedOut;

        internal ExactSizedBufferWriter(int slack) => _slack = slack;

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
