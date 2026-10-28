using System.Globalization;

namespace PicoJetson.Tests;

/// <summary>One decoded token, captured as text so two paths can be compared.</summary>
public readonly record struct JsonTokenSnapshot(
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
            Index.ToString(CultureInfo.InvariantCulture),
            Type.ToString(),
            Consumed.ToString(CultureInfo.InvariantCulture),
            Depth.ToString(CultureInfo.InvariantCulture),
            Raw,
            Scalars
        );
}

/// <summary>
/// Task T12 (fused token read, spec §5.2 / §6 S2): the JSON corpus and the token-capture fixture the
/// dual-walk guard compares against.
/// <para>
/// <b>Why a second corpus instead of the MessagePack one.</b> The guard for a format can only compare the
/// token families that format's reader emits. JSON has no <c>nil</c>/<c>bin</c>/<c>ext</c> tags and no
/// counted collection header; it has <c>PropertyName</c> tokens, escape-decoded strings, and the
/// strict-JSON number classification (int32 / int64 / uint64 / float64). Copying MessagePack's bytes would
/// pin none of that (plan T12: "按 JSON 的 token 家族取 payload，而不是照抄 MsgPack 的字节").
/// </para>
/// <para>
/// <b>Static and test-framework free</b>, like <c>MsgPackSpanTokenDump</c>: the corpus and
/// <see cref="Capture"/> are shared by the reference arm, the fused arm, and the negative control, so a
/// difference can only come from the reader, never from the dump.
/// </para>
/// </summary>
public static class JsonSpanTokenDump
{
    /// <summary>Well-formed payloads covering every JSON token family the reader emits.</summary>
    public static IEnumerable<(string Name, byte[] Payload)> Payloads()
    {
        // ── literals ──
        yield return ("null", U8("null"));
        yield return ("bool-true", U8("true"));
        yield return ("bool-false", U8("false"));

        // ── integers: sign, int32 boundary, int64 boundary, beyond ──
        yield return ("int-zero", U8("0"));
        yield return ("int-neg-zero", U8("-0"));
        yield return ("int-positive", U8("42"));
        // F-1's seed (spec §6 S2 / plan T13): a negative number, where the sequence path used to drop the sign.
        yield return ("int-negative", U8("-2"));
        yield return ("int32-min", U8("-2147483648"));
        yield return ("int32-max", U8("2147483647"));
        yield return ("int32-overflow-positive", U8("2147483648"));
        yield return ("int32-overflow-negative", U8("-2147483649"));
        yield return ("int64-max", U8("9223372036854775807"));
        yield return ("int64-min", U8("-9223372036854775808"));
        yield return ("int64-overflow-to-uint64", U8("9223372036854775808"));
        yield return ("uint64-max", U8("18446744073709551615"));
        yield return ("beyond-uint64", U8("99999999999999999999999999"));

        // ── floats: fraction, sign, exponent, negative zero ──
        yield return ("float-simple", U8("1.5"));
        yield return ("float-negative", U8("-1.5"));
        yield return ("float-neg-zero", U8("-0.0"));
        yield return ("float-exponent", U8("1e10"));
        yield return ("float-exponent-plus", U8("1.5E+3"));
        yield return ("float-exponent-minus", U8("-2.5e-7"));
        yield return ("float-many-digits", U8("0.1234567890123456789"));

        // ── strings: escapes, \uXXXX, non-ASCII, emoji, surrogate pair ──
        yield return ("string-empty", U8("\"\""));
        yield return ("string-ascii", U8("\"hello\""));
        yield return ("string-escapes", U8("\"a\\\"b\\\\c\\/d\\b\\f\\n\\r\\t\""));
        yield return ("string-unicode-escapes", U8("\"\\u00e9\\u4e2d\""));
        yield return ("string-surrogate-pair", U8("\"\\ud83d\\ude00\""));
        yield return ("string-raw-utf8-non-ascii", U8("\"héllo 中文\""));
        yield return ("string-raw-emoji", U8("\"😀\""));
        yield return ("string-long-1024", U8("\"" + new string('x', 1024) + "\""));

        // ── containers: empty, single element, mixed families, nesting ──
        yield return ("object-empty", U8("{}"));
        yield return ("object-one", U8("{\"a\":1}"));
        yield return (
            "object-all-scalar-families",
            U8(
                "{\"s\":\"x\",\"i\":1,\"big\":9223372036854775807,"
                    + "\"u\":18446744073709551615,\"f\":1.5,\"b\":true,\"n\":null}"
            )
        );
        yield return ("object-unicode-key", U8("{\"中文\":\"值\"}"));
        yield return ("object-nested-3", U8("{\"a\":{\"b\":{\"c\":1}}}"));
        yield return ("array-empty", U8("[]"));
        yield return ("array-one", U8("[1]"));
        yield return ("array-ints", U8("[0,-1,-2,42,-2147483648,2147483647]"));
        yield return ("array-mixed-families", U8("[null,true,false,0,-0,1.5,\"s\",[],{}]"));
        yield return ("array-of-objects", U8("[{\"a\":1},{\"b\":2}]"));
        yield return ("array-of-arrays", U8("[[],[[]],[[[]]]]"));
        yield return ("array-strings", U8("[\"a\",\"\",\"\\u00e9\"]"));

        // ── layout: whitespace and depth (default maxDepth is 256) ──
        yield return ("whitespace-everywhere", U8("{ \"a\" : [ 1 , 2 ] , \"b\" : { } }"));
        yield return ("trailing-whitespace", U8("{\"a\":1}  \n\t "));
        yield return ("nested-arrays-depth-32", U8(Repeat('[', 32) + "1" + Repeat(']', 32)));
        yield return ("nested-objects-depth-16", U8(Repeat("{\"a\":", 16) + "1" + Repeat('}', 16)));
        yield return (
            "flat-object-32-members",
            U8(
                "{"
                    + string.Join(
                        ",",
                        Enumerable.Range(0, 32).Select(i => $"\"k{i}\":{i * 7 - 100}")
                    )
                    + "}"
            )
        );
    }

    /// <summary>
    /// The malformed set. <c>ThrowsInSpan</c> is measured, not assumed: the span path is strict where the
    /// sequence path is allowed to pause, so the flag records which entries really fail in the strict mode
    /// the test asserts it in. It is a corpus fact (pinned by a test), never an input to the comparison —
    /// both arms are walked over the same partial token stream regardless.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Payload, bool ThrowsInSpan)> MalformedPayloads()
    {
        // Truncation: the span path tolerates an unterminated container (it simply runs out of
        // input); the string/key paths fail loudly. Recorded per entry, both arms compared regardless.
        yield return ("unclosed-object", U8("{\"a\":1"), false);
        yield return ("unclosed-nested-object", U8("{\"a\":{\"b\":1}"), false);
        yield return ("unclosed-array", U8("[1,2"), false);
        yield return ("unclosed-string", U8("{\"a\":\"x"), true);
        yield return ("unclosed-key", U8("{\"a"), true);
        yield return ("trailing-comma-array", U8("[1,2,]"), true);
        yield return ("trailing-comma-object", U8("{\"a\":1,}"), true);
        yield return ("double-comma-array", U8("[1,,2]"), true);
        yield return ("leading-comma-array", U8("[,1]"), true);
        yield return ("leading-comma-object", U8("{,\"a\":1}"), true);
        yield return ("invalid-escape", U8("{\"a\":\"\\q\"}"), true);
        yield return ("unfinished-unicode-escape", U8("{\"a\":\"\\u00\"}"), true);
        yield return ("bad-literal-tru", U8("tru"), true);
        yield return ("bad-literal-nul", U8("nul"), true);
        // A literal whose first byte is right and whose tail is not, placed AFTER a token has been read:
        // only a reader that compares every byte (rather than the opening one) rejects these, and the
        // token before it is what lets the fused arm be walked up to the failure instead of not at all.
        yield return ("bad-literal-after-value", U8("[1,tXue]"), true);
        yield return ("bad-literal-f-after-value", U8("[1,falsx]"), true);
        // A bare 0x01 inside a string is invalid JSON (RFC 8259 §7) but this reader tolerates it, so
        // the entry is recorded as "no throw" — the guard dumps the token either way.
        yield return ("raw-control-char-in-string", U8("{\"a\":\"x\u0001y\"}"), false);
        yield return ("unquoted-key", U8("{a:1}"), true);
        yield return ("missing-value", U8("{\"a\":}"), false);
        yield return ("lone-closing-bracket", U8("]"), true);
        yield return ("unexpected-byte", U8("{\"a\":1}@"), true);
        yield return ("trailing-garbage", U8("{\"a\":1}x"), true);
        yield return ("two-roots", U8("{} {}"), false);
        yield return ("leading-zero", U8("01"), true);
        yield return ("float-missing-fraction", U8("{\"a\":1.}"), true);
        yield return ("depth-overflow-257", U8(Repeat('[', 257) + Repeat(']', 257)), true);
    }

    public static byte[] PayloadByName(string name) =>
        Payloads()
            .Concat(MalformedPayloads().Select(m => (m.Name, m.Payload)))
            .First(p => p.Name == name)
            .Payload;

    /// <summary>
    /// The deterministic random corpus: the part that reaches nesting and token combinations the named
    /// matrix does not enumerate. Fixed seed and fixed size, so a green guard is reproducible.
    /// </summary>
    public static IEnumerable<(string Name, byte[] Payload)> RandomPayloads(
        int seed = 20261028,
        int count = 300
    )
    {
        var rng = new Random(seed);
        for (int i = 0; i < count; i++)
        {
            var text = new StringBuilder();
            EmitValue(text, rng, depth: 5);
            yield return ($"random-{i}", U8(text.ToString()));
        }
    }

    /// <summary>Payloads short enough to be sliced byte by byte in the chunked-streaming sweep.</summary>
    public static IEnumerable<(string Name, byte[] Payload)> SmallPayloads() =>
        Payloads().Where(p => p.Payload.Length <= 64);

    /// <summary>Span mode: the whole payload as one span (the reference reader constructor).</summary>
    public static List<JsonTokenSnapshot> DrainSpan(byte[] payload, bool isFinalBlock = true)
    {
        var tokens = new List<JsonTokenSnapshot>();
        // Not a `using` variable: Capture() takes the reader by ref, which the language forbids there.
        var reader = new JsonReader(payload.AsSpan(), maxDepth: 256, isFinalBlock: isFinalBlock);
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

    /// <summary>Sequence mode: the whole payload as one single-segment sequence.</summary>
    public static List<JsonTokenSnapshot> DrainSequence(byte[] payload, bool isFinalBlock = true)
    {
        var tokens = new List<JsonTokenSnapshot>();
        var reader = new JsonReader(
            new ReadOnlySequence<byte>(payload),
            maxDepth: 256,
            isFinalBlock: isFinalBlock
        );
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

    /// <summary>Snapshot of the reader's current token (every getter the contract exposes, fixed order).</summary>
    public static JsonTokenSnapshot Capture(int index, ref JsonReader reader)
    {
        var raw = reader.GetStringRaw();
        var scalars = new List<string>(4);
        if (reader.TryGetInt32(out var i32))
            scalars.Add("i32=" + i32.ToString(CultureInfo.InvariantCulture));
        if (reader.TryGetInt64(out var i64))
            scalars.Add("i64=" + i64.ToString(CultureInfo.InvariantCulture));
        if (reader.TryGetUInt64(out var u64))
            scalars.Add("u64=" + u64.ToString(CultureInfo.InvariantCulture));
        if (reader.TryGetFloat64(out var f64))
            scalars.Add(
                "f64="
                    + BitConverter
                        .DoubleToInt64Bits(f64)
                        .ToString("X16", CultureInfo.InvariantCulture)
            );
        if (reader.TryGetBool(out var b))
            scalars.Add("bool=" + (b ? "1" : "0"));
        return new JsonTokenSnapshot(
            index,
            reader.TokenType,
            reader.BytesConsumed,
            reader.Depth,
            Hex(raw),
            string.Join(",", scalars)
        );
    }

    /// <summary>Hex of a value span, truncated in the middle so a long string token stays readable.</summary>
    public static string Hex(ReadOnlySpan<byte> span)
    {
        const int head = 32;
        const int tail = 32;
        if (span.Length <= head + tail)
            return Convert.ToHexString(span);
        return string.Concat(
            "len=",
            span.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            Convert.ToHexString(span[..head]),
            "..",
            Convert.ToHexString(span[^tail..])
        );
    }

    // ── helpers ──

    private static byte[] U8(string text) => Encoding.UTF8.GetBytes(text);

    private static string Repeat(char c, int count) => new(c, count);

    private static string Repeat(string s, int count) => string.Concat(Enumerable.Repeat(s, count));

    /// <summary>
    /// One random JSON value. Strings are drawn from a table of the shapes that actually exercise the
    /// reader (escapes, <c>\uXXXX</c>, surrogate pairs, raw non-ASCII), not from printable ASCII only —
    /// a generator that only emits <c>"abc"</c> would leave the escape path untested.
    /// </summary>
    private static void EmitValue(StringBuilder into, Random rng, int depth)
    {
        // Containers are deliberately over-represented (3 of 10 kinds, and every array/object has up to
        // three children): a generator that emits mostly scalars yields a handful of tokens per payload
        // and would leave nesting, PropertyName and the closing tokens barely covered.
        int kinds = depth <= 0 ? 7 : 10;
        switch (rng.Next(0, kinds))
        {
            case 0:
                into.Append("null");
                return;
            case 1:
                into.Append(rng.Next(2) == 0 ? "true" : "false");
                return;
            case 2:
                into.Append(rng.Next(-1000, 1000).ToString(CultureInfo.InvariantCulture));
                return;
            case 3:
                into.Append(
                    rng.NextInt64(-5000000000L, 5000000000L).ToString(CultureInfo.InvariantCulture)
                );
                return;
            case 4:
                into.Append(
                    rng.NextInt64(long.MinValue, long.MaxValue)
                        .ToString(CultureInfo.InvariantCulture)
                );
                return;
            case 5:
                into.Append(
                    (rng.NextDouble() * 1e6 - 5e5).ToString("R", CultureInfo.InvariantCulture)
                );
                return;
            case 6:
                into.Append(StringToken(rng));
                return;
            case 7:
            case 8:
            {
                into.Append('[');
                int elements = rng.Next(0, 4);
                for (int i = 0; i < elements; i++)
                {
                    if (i > 0)
                        into.Append(',');
                    EmitValue(into, rng, depth - 1);
                }
                into.Append(']');
                return;
            }
            default:
                into.Append('{');
                int members = rng.Next(0, 4);
                for (int i = 0; i < members; i++)
                {
                    if (i > 0)
                        into.Append(',');
                    into.Append("\"k")
                        .Append(i.ToString(CultureInfo.InvariantCulture))
                        .Append("\":");
                    EmitValue(into, rng, depth - 1);
                }
                into.Append('}');
                return;
        }
    }

    private static readonly string[] RandomStringTokens =
    [
        "\"\"",
        "\"a\"",
        "\"a\\\"b\"",
        "\"\\u00e9\"",
        "\"\\ud83d\\ude00\"",
        "\"中文\"",
        "\"tab\\there\"",
        "\"back\\\\slash\"",
        "\"😀\"",
    ];

    private static string StringToken(Random rng) =>
        RandomStringTokens[rng.Next(0, RandomStringTokens.Length)];
}
