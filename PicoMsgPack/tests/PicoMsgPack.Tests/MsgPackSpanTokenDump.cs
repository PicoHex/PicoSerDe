namespace PicoMsgPack.Tests;

/// <summary>One decoded token, captured as text so two paths can be compared.</summary>
public readonly record struct MsgPackTokenSnapshot(
    int Index,
    TokenType Type,
    long Consumed,
    int Depth,
    string Raw,
    string Scalars
)
{
    public override string ToString() =>
        string.Join(
            "|",
            Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Type.ToString(),
            Consumed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Raw,
            Scalars
        );
}

/// <summary>
/// Task 4e (MsgPack read-side span fast path) shared fixture.
/// <para>
/// <see cref="MsgPackReader.Read"/> now dispatches: a span-mode buffer takes
/// the new <c>ReadSpan</c> path, a <see cref="ReadOnlySequence{T}"/> takes the
/// original <c>ReadSequential</c> path. The only honest way to show the two are
/// interchangeable is to decode the same payload both ways and compare the
/// token streams property by property, which is what this file builds.
/// </para>
/// <para>
/// It is deliberately free of any test-framework type: the out-of-tree golden
/// dump (<c>.superpowers/sdd/perf-mp-readspan/harness/DumpTokens</c>) compiles
/// this same file, so the "before the change" and "after the change" dumps are
/// produced from one payload matrix rather than two that could drift apart.
/// </para>
/// </summary>
public static class MsgPackSpanTokenDump
{
    /// <summary>Well-formed payloads covering every token family the reader emits.</summary>
    public static IEnumerable<(string Name, byte[] Payload)> Payloads()
    {
        // ── scalars: nil, bool, integers, floats ──
        yield return ("nil", new byte[] { 0xC0 });
        yield return ("bool-true", new byte[] { 0xC3 });
        yield return ("bool-false", new byte[] { 0xC2 });
        yield return ("fixint-0", new byte[] { 0x00 });
        yield return ("fixint-127", new byte[] { 0x7F });
        yield return ("negfixint-minus1", new byte[] { 0xFF });
        yield return ("negfixint-minus32", new byte[] { 0xE0 });
        yield return ("uint8-155", new byte[] { 0xCC, 0x9B });
        yield return ("uint16-256", new byte[] { 0xCD, 0x01, 0x00 });
        yield return ("uint32-65536", new byte[] { 0xCE, 0x00, 0x01, 0x00, 0x00 });
        yield return (
            "uint64-max",
            new byte[] { 0xCF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }
        );
        yield return ("int8-minus128", new byte[] { 0xD0, 0x80 });
        yield return ("int16-300", new byte[] { 0xD1, 0x01, 0x2C });
        yield return ("int32-minus100000", new byte[] { 0xD2, 0xFF, 0xFE, 0x79, 0x60 });
        yield return (
            "int64-min",
            new byte[] { 0xD3, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
        );
        yield return ("float32-1.5", new byte[] { 0xCA, 0x3F, 0xC0, 0x00, 0x00 });
        yield return (
            "float64-1.5",
            new byte[] { 0xCB, 0x3F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }
        );

        // ── strings: fixstr / str8 / str16 / str32, empty and multi-byte ──
        yield return ("fixstr-empty", new byte[] { 0xA0 });
        yield return ("fixstr-ascii-1", new byte[] { 0xA1, (byte)'a' });
        yield return ("fixstr-ascii-31", FixStr(Ascii(31, 'a')));
        yield return ("fixstr-utf8-3byte-cjk", FixStr(Encoding.UTF8.GetBytes("日本語")));
        yield return ("str8-32", Str8(Ascii(32, 'b')));
        yield return ("str8-255", Str8(Ascii(255, 'c')));
        yield return ("str16-300", Str16(Ascii(300, 'd')));
        yield return ("str32-70000", Str32(Ascii(70000, 'e')));

        // ── bin8 / bin16 / bin32 (16772 bytes: the large-bin regression size) ──
        yield return ("bin8-empty", new byte[] { 0xC4, 0x00 });
        yield return ("bin8-3", new byte[] { 0xC4, 0x03, 0x01, 0x02, 0x03 });
        yield return ("bin16-300", Bin16(Sequence(300, 1)));
        yield return ("bin32-16772", Bin32(Sequence(16772, 7)));

        // ── arrays: fixarray / array16 / array32 ──
        yield return ("fixarray-empty", new byte[] { 0x90 });
        yield return ("fixarray-3", new byte[] { 0x93, 0x01, 0x02, 0x03 });
        yield return ("fixarray-15-nil", FixArray15Nils());
        yield return ("array16-300-nil", Array16(300));
        yield return ("array32-65536-nil", Array32(65536));

        // ── maps: fixmap / map16 / map32 ──
        yield return ("fixmap-empty", new byte[] { 0x80 });
        yield return ("fixmap-2", new byte[] { 0x82, 0x00, 0x01, 0x01, 0x02 });
        yield return ("map16-20", Map16(20));
        yield return ("map32-65536-nil", Map32(65536));

        // ── fixext / ext8 / ext16 / ext32 ──
        yield return ("fixext1", new byte[] { 0xD4, 0x07, 0x42 });
        yield return ("fixext2", new byte[] { 0xD5, 0x07, 0x01, 0x02 });
        yield return ("fixext4", new byte[] { 0xD6, 0x07, 0x01, 0x02, 0x03, 0x04 });
        yield return (
            "fixext8",
            new byte[] { 0xD7, 0x07, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 }
        );
        yield return (
            "fixext16",
            new byte[]
            {
                0xD8,
                0x07,
                0x01,
                0x02,
                0x03,
                0x04,
                0x05,
                0x06,
                0x07,
                0x08,
                0x09,
                0x0A,
                0x0B,
                0x0C,
                0x0D,
                0x0E,
                0x0F,
                0x10,
            }
        );
        yield return ("ext8-empty", new byte[] { 0xC7, 0x00, 0x07 });
        yield return ("ext8-3", new byte[] { 0xC7, 0x03, 0x07, 0x01, 0x02, 0x03 });
        yield return ("ext16-300", Ext16(Sequence(300, 3)));
        yield return ("ext32-70000", Ext32(Sequence(70000, 5)));

        // ── timestamp extension (Task 4d): 32-, 64- and 96-bit forms ──
        yield return ("timestamp32", new byte[] { 0xD6, 0xFF, 0x6A, 0xDF, 0x40, 0xC0 });
        yield return ("timestamp64", Timestamp64());
        yield return ("timestamp96", Timestamp96());

        // ── nesting / realistic shapes ──
        yield return (
            "nested-map-array",
            new byte[] { 0x82, 0x00, 0x93, 0x01, 0x02, 0x03, 0x01, 0x81, 0x00, 0xA1, (byte)'x' }
        );
        yield return ("nested-array-depth-60", NestedArrays(60));
        yield return ("map-10-int-pairs", MapTenInts());
    }

    /// <summary>
    /// Malformed payloads: the reader must fail on them, and it must fail the
    /// same way before and after Task 4e (same exception, same message) in each
    /// mode. They are kept out of the token matrix because a throw ends the
    /// token stream.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Payload)> MalformedPayloads()
    {
        yield return ("trailing-garbage", new byte[] { 0x81, 0x00, 0x01, 0xFF });
        yield return ("unknown-byte-c1", new byte[] { 0xC1 });
        yield return ("truncated-int32", new byte[] { 0xD2, 0x00, 0x01 });
        yield return ("truncated-length-bytes-str16", new byte[] { 0xDA, 0x01 });
        yield return ("hostile-bin32-length", new byte[] { 0xC6, 0xFF, 0xFF, 0xFF, 0xFF });
        yield return ("truncated-fixext-tag", new byte[] { 0xD6 });
        yield return ("truncated-str8-body", new byte[] { 0xD9, 0x0A, 0x61, 0x62, 0x63 });
        yield return ("depth-overflow-65", NestedArrays(65));
    }

    /// <summary>One well-formed payload by name (data sources pass names, not buffers).</summary>
    public static byte[] PayloadByName(string name) =>
        Payloads().First(p => p.Name == name).Payload;

    /// <summary>
    /// Deterministic corpus of *well-formed* random payloads: every token is
    /// self-consistent (exact length prefixes, containers closed), so both token
    /// paths must walk them to the end with identical tokens and no exception.
    /// This is the part of the guardrail that reaches combinations the named
    /// matrix above does not enumerate.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Payload)> RandomPayloads(
        int count = 2000,
        int seed = 20261027
    )
    {
        for (int i = 0; i < count; i++)
        {
            var rng = new Random(seed + i);
            var bytes = new List<byte>(64);
            EmitToken(bytes, rng, depth: 0);
            yield return (
                "random-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                bytes.ToArray()
            );
        }
    }

    private static void EmitToken(List<byte> into, Random rng, int depth)
    {
        // Leaves are always available; containers only while there is depth left.
        var kind = rng.Next(0, depth < 3 ? 22 : 18);
        switch (kind)
        {
            case 0:
                into.Add((byte)rng.Next(0, 0x80)); // positive fixint
                return;
            case 1:
                into.Add((byte)rng.Next(0xE0, 0x100)); // negative fixint
                return;
            case 2:
                into.Add(0xC0); // nil
                return;
            case 3:
                into.Add(rng.Next(0, 2) == 0 ? (byte)0xC2 : (byte)0xC3); // bool
                return;
            case 4:
                into.Add(0xCC);
                into.Add((byte)rng.Next(0, 0x100)); // uint8
                return;
            case 5:
                into.Add(0xCD);
                AppendBigEndian(into, (uint)rng.Next(0, 0x10000), 2);
                return;
            case 6:
                into.Add(0xCE);
                AppendBigEndian(into, (uint)rng.Next(), 4);
                return;
            case 7:
                into.Add(0xD0);
                into.Add((byte)rng.Next(0x80, 0x100)); // int8 negative
                return;
            case 8:
                into.Add(0xD2);
                AppendBigEndian(into, (uint)rng.Next(), 4);
                return;
            case 9:
                into.Add(0xD3);
                AppendBigEndian(into, (uint)rng.Next(), 4);
                AppendBigEndian(into, (uint)rng.Next(), 4);
                return;
            case 10:
                into.Add(0xCA);
                AppendBigEndian(into, (uint)rng.Next(), 4);
                return;
            case 11:
                into.Add(0xCB);
                AppendBigEndian(into, (ulong)rng.NextInt64(), 8);
                return;
            case 12:
            {
                // fixstr / str8 / str16 with a mix of ASCII and 3-byte UTF-8
                var length = rng.Next(0, 40);
                var body = new byte[length];
                for (int i = 0; i < length; i++)
                    body[i] = (byte)(rng.Next(0, 2) == 0 ? 0x41 + rng.Next(0, 26) : 0xE3);
                if (length <= 31)
                {
                    into.Add((byte)(0xA0 | length));
                }
                else
                {
                    into.Add(0xD9);
                    into.Add((byte)length);
                }
                into.AddRange(body);
                return;
            }
            case 13:
            {
                var length = rng.Next(0, 40);
                into.Add(0xC4); // bin8
                into.Add((byte)length);
                for (int i = 0; i < length; i++)
                    into.Add((byte)rng.Next());
                return;
            }
            case 14:
            {
                var length = rng.Next(0, 20);
                into.Add(0xC7); // ext8
                into.Add((byte)length);
                into.Add((byte)rng.Next());
                for (int i = 0; i < length; i++)
                    into.Add((byte)rng.Next());
                return;
            }
            case 15:
            {
                // timestamp-32 / timestamp-64 / timestamp-96 shaped extension
                switch (rng.Next(0, 3))
                {
                    case 0:
                        into.Add(0xD6);
                        into.Add(0xFF);
                        AppendBigEndian(into, (uint)rng.Next(), 4);
                        return;
                    case 1:
                        into.Add(0xD7);
                        into.Add(0xFF);
                        AppendBigEndian(into, (ulong)rng.NextInt64(), 8);
                        return;
                    default:
                        into.Add(0xC7);
                        into.Add(0x0C);
                        into.Add(0xFF);
                        for (int i = 0; i < 12; i++)
                            into.Add((byte)rng.Next());
                        return;
                }
            }
            case 16:
                into.Add(0xD4); // fixext1
                into.Add((byte)rng.Next());
                into.Add((byte)rng.Next());
                return;
            case 17:
                into.Add(0xD8); // fixext16
                into.Add((byte)rng.Next());
                for (int i = 0; i < 16; i++)
                    into.Add((byte)rng.Next());
                return;
            case 18:
            case 19:
            {
                // fixarray of 0..3 elements
                var count = rng.Next(0, 4);
                into.Add((byte)(0x90 | count));
                for (int i = 0; i < count; i++)
                    EmitToken(into, rng, depth + 1);
                return;
            }
            default:
            {
                // fixmap of 0..2 pairs; keys are ints, values are any token
                var pairs = rng.Next(0, 3);
                into.Add((byte)(0x80 | pairs));
                for (int i = 0; i < pairs; i++)
                {
                    into.Add((byte)rng.Next(0, 0x80));
                    EmitToken(into, rng, depth + 1);
                }
                return;
            }
        }
    }

    private static void AppendBigEndian(List<byte> into, ulong value, int bytes)
    {
        for (int i = bytes - 1; i >= 0; i--)
            into.Add((byte)(value >> (i * 8)));
    }

    /// <summary>Payloads small enough for byte-by-byte chunking in streaming tests.</summary>
    public static IEnumerable<(string Name, byte[] Payload)> SmallPayloads() =>
        Payloads().Where(p => p.Payload.Length <= 512);

    /// <summary>Decodes the payload from a buffer: this is the span path.</summary>
    public static List<MsgPackTokenSnapshot> DrainSpan(byte[] payload, bool isFinalBlock = true)
    {
        var tokens = new List<MsgPackTokenSnapshot>();
        // Not a `using` variable: Capture() takes the reader by ref, which the
        // language forbids for using-locals.
        var reader = new MsgPackReader(payload.AsSpan(), isFinalBlock);
        try
        {
            while (reader.Read())
                tokens.Add(Capture(tokens.Count, ref reader));
        }
        finally
        {
            reader.Dispose();
        }
        return tokens;
    }

    /// <summary>Decodes the payload from a single-segment sequence: the sequence path.</summary>
    public static List<MsgPackTokenSnapshot> DrainSequence(byte[] payload, bool isFinalBlock = true)
    {
        var tokens = new List<MsgPackTokenSnapshot>();
        var reader = new MsgPackReader(new ReadOnlySequence<byte>(payload), isFinalBlock);
        try
        {
            while (reader.Read())
                tokens.Add(Capture(tokens.Count, ref reader));
        }
        finally
        {
            reader.Dispose();
        }
        return tokens;
    }

    /// <summary>
    /// Reproduces what <c>MsgPackSerializer.DeserializeStreamingCore</c> does:
    /// accumulate one chunk, re-parse the accumulated prefix from the start,
    /// stop as soon as the document parses completely. Returns the token stream
    /// of that final, successful attempt.
    /// </summary>
    public static List<MsgPackTokenSnapshot> DrainStreaming(byte[] payload, int chunkSize)
    {
        for (int taken = Math.Min(chunkSize, payload.Length); ; taken += chunkSize)
        {
            var isFinalBlock = taken >= payload.Length;
            var prefix = payload.AsSpan(0, Math.Min(taken, payload.Length)).ToArray();
            var tokens = new List<MsgPackTokenSnapshot>();
            var complete = false;
            var reader = new MsgPackReader(new ReadOnlySequence<byte>(prefix), isFinalBlock);
            try
            {
                while (reader.Read())
                    tokens.Add(Capture(tokens.Count, ref reader));
                complete =
                    !reader.NeedsMoreData
                    && reader.Depth == 0
                    && reader.BytesConsumed == prefix.Length;
            }
            finally
            {
                reader.Dispose();
            }
            if (complete)
                return tokens;
            if (isFinalBlock)
                throw new FormatException(
                    $"streaming never completed the {payload.Length}-byte payload "
                        + $"(chunk {chunkSize}, {tokens.Count} tokens)"
                );
        }
    }

    /// <summary>Snapshot of the reader's current token (all getters, in a fixed order).</summary>
    public static MsgPackTokenSnapshot Capture(int index, ref MsgPackReader reader)
    {
        var raw = reader.GetStringRaw();
        var scalars = new List<string>(4);
        if (reader.TryGetInt32(out var i32))
            scalars.Add("i32=" + i32.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (reader.TryGetInt64(out var i64))
            scalars.Add("i64=" + i64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (reader.TryGetUInt64(out var u64))
            scalars.Add("u64=" + u64.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (reader.TryGetFloat64(out var f64))
            scalars.Add(
                "f64="
                    + BitConverter
                        .DoubleToInt64Bits(f64)
                        .ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            );
        if (reader.TryGetBool(out var b))
            scalars.Add("bool=" + (b ? "1" : "0"));
        if (reader.TryGetExtension(out var tag, out var ext))
            scalars.Add(
                "ext="
                    + tag.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)
                    + ":"
                    + Hex(ext)
            );
        return new MsgPackTokenSnapshot(
            index,
            reader.TokenType,
            reader.BytesConsumed,
            reader.Depth,
            Hex(raw),
            string.Join(",", scalars)
        );
    }

    /// <summary>
    /// Hex of a value span, truncated in the middle for large payloads: long
    /// enough to prove content equality, short enough that a 70 kB token does
    /// not turn the dump into megabytes.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> span)
    {
        const int head = 32;
        const int tail = 32;
        if (span.Length <= head + tail)
            return Convert.ToHexString(span);
        return string.Concat(
            "len=",
            span.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            Convert.ToHexString(span[..head]),
            "..",
            Convert.ToHexString(span[^tail..])
        );
    }

    // ── payload builders ──

    private static byte[] Ascii(int length, char c)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)c);
        return bytes;
    }

    private static byte[] Sequence(int length, byte first)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(first + (i & 0x3F));
        return bytes;
    }

    private static byte[] FixStr(byte[] body) => Frame([(byte)(0xA0 | body.Length)], body);

    private static byte[] Str8(byte[] body) => Frame([0xD9, (byte)body.Length], body);

    private static byte[] Str16(byte[] body) =>
        Frame([0xDA, (byte)(body.Length >> 8), (byte)body.Length], body);

    private static byte[] Str32(byte[] body) =>
        Frame(
            [
                0xDB,
                (byte)(body.Length >> 24),
                (byte)(body.Length >> 16),
                (byte)(body.Length >> 8),
                (byte)body.Length,
            ],
            body
        );

    private static byte[] Bin16(byte[] body) =>
        Frame([0xC5, (byte)(body.Length >> 8), (byte)body.Length], body);

    private static byte[] Bin32(byte[] body) =>
        Frame(
            [
                0xC6,
                (byte)(body.Length >> 24),
                (byte)(body.Length >> 16),
                (byte)(body.Length >> 8),
                (byte)body.Length,
            ],
            body
        );

    private static byte[] Ext16(byte[] body) =>
        Frame([0xC8, (byte)(body.Length >> 8), (byte)body.Length, 0x07], body);

    private static byte[] Ext32(byte[] body) =>
        Frame(
            [
                0xC9,
                (byte)(body.Length >> 24),
                (byte)(body.Length >> 16),
                (byte)(body.Length >> 8),
                (byte)body.Length,
                0x07,
            ],
            body
        );

    private static byte[] FixArray15Nils()
    {
        var bytes = new byte[16];
        bytes[0] = 0x9F;
        for (int i = 1; i < bytes.Length; i++)
            bytes[i] = 0xC0;
        return bytes;
    }

    private static byte[] Array16(int count)
    {
        var bytes = new byte[3 + count];
        bytes[0] = 0xDC;
        bytes[1] = (byte)(count >> 8);
        bytes[2] = (byte)count;
        Array.Fill(bytes, (byte)0xC0, 3, count);
        return bytes;
    }

    private static byte[] Array32(int count)
    {
        var bytes = new byte[5 + count];
        bytes[0] = 0xDD;
        bytes[1] = (byte)(count >> 24);
        bytes[2] = (byte)(count >> 16);
        bytes[3] = (byte)(count >> 8);
        bytes[4] = (byte)count;
        Array.Fill(bytes, (byte)0xC0, 5, count);
        return bytes;
    }

    private static byte[] Map16(int pairs)
    {
        var bytes = new byte[3 + (pairs * 2)];
        bytes[0] = 0xDE;
        bytes[1] = (byte)(pairs >> 8);
        bytes[2] = (byte)pairs;
        for (int i = 0; i < pairs; i++)
        {
            // keys are small fixints, values are nil
            bytes[3 + (i * 2)] = (byte)i;
            bytes[4 + (i * 2)] = 0xC0;
        }
        return bytes;
    }

    private static byte[] Map32(int pairs)
    {
        var bytes = new byte[5 + (pairs * 2)];
        bytes[0] = 0xDF;
        bytes[1] = (byte)(pairs >> 24);
        bytes[2] = (byte)(pairs >> 16);
        bytes[3] = (byte)(pairs >> 8);
        bytes[4] = (byte)pairs;
        Array.Fill(bytes, (byte)0xC0, 5, pairs * 2);
        return bytes;
    }

    /// <summary>Depth <paramref name="depth"/> nested one-element arrays around a fixint 0.</summary>
    private static byte[] NestedArrays(int depth)
    {
        var bytes = new byte[depth + 1];
        Array.Fill(bytes, (byte)0x91, 0, depth);
        bytes[depth] = 0x00;
        return bytes;
    }

    /// <summary>The wire shape the generator produces for a ten-int object (map, int keys).</summary>
    private static byte[] MapTenInts()
    {
        var bytes = new byte[1 + 20];
        bytes[0] = 0x8A;
        for (int i = 0; i < 10; i++)
        {
            bytes[1 + (i * 2)] = (byte)i;
            bytes[2 + (i * 2)] = (byte)(i * 7);
        }
        return bytes;
    }

    private static byte[] Timestamp64()
    {
        const long nsec = 500_000_000;
        const long sec = 0x5A1F2C0;
        var v = (nsec << 34) | sec;
        var body = new byte[8];
        for (int i = 0; i < 8; i++)
            body[i] = (byte)(v >> (56 - (i * 8)));
        return Frame([0xD7, 0xFF], body);
    }

    private static byte[] Timestamp96() =>
        // ext8 of length 12, tag 0xFF: 4-byte nanoseconds + 8-byte seconds
        Frame(
            [0xC7, 0x0C, 0xFF],
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x6A, 0xDF, 0x40, 0xC0 }
        );

    private static byte[] Frame(byte[] header, byte[] body)
    {
        var bytes = new byte[header.Length + body.Length];
        header.CopyTo(bytes, 0);
        body.CopyTo(bytes, header.Length);
        return bytes;
    }
}
